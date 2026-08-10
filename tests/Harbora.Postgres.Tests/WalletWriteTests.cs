using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The two things that keep a balance honest when more than one writer reaches it, neither of which
/// exists outside a real database.
///
/// <para>
/// <c>WalletServiceTests</c> asserts that a credit rotates <c>Wallet.ConcurrencyStamp</c> and says in
/// as many words that it is asserting the rotation "rather than a race this lane cannot stage". This
/// is the lane that can. The rotation is only half the mechanism: EF compares the token it read
/// against the row it is updating, so the other half is the database refusing an <c>UPDATE</c> whose
/// <c>WHERE</c> no longer matches — and that half has never been asked of anything.
/// </para>
///
/// <para>
/// The stakes are why it is worth a container. A concurrency token that does not fire is not a
/// visible failure: both writers report success, and one of the two movements simply is not in the
/// balance. The hourly pass takes money out and an administrator's credit puts it in, they collide on
/// exactly the row this protects, and whichever loses does so silently. <c>BillingTick.SaveAsync</c>
/// is built entirely around the assumption that it will be told — it catches the conflict, reloads,
/// and re-applies its own movement on top of the other writer's.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class WalletWriteTests(PostgresLane lane)
{
    private static readonly Guid Workspace = new("41111111-0000-0000-0000-000000000001");

    [PostgresFact]
    public async Task A_workspace_cannot_have_two_wallets()
    {
        // Not decoration on the model. BillingTick creates the wallet lazily, on the first hour a
        // workspace is charged rather than at sign-up, so two passes reaching that first hour at the
        // same moment both insert — and BillingTick.SaveAsync says so, then goes to the trouble of
        // asking the database whether the hour is paid for instead of reading 23505 as "already
        // charged". Without this index the second insert succeeds, the workspace has two balances,
        // and SUM(ledger) stops agreeing with either of them.
        await using var db = PostgresLane.Open(await lane.FreshlyMigratedAsync("wallet_unique"));

        db.Wallets.Add(new Wallet { WorkspaceId = Workspace });
        await db.SaveChangesAsync();

        db.Wallets.Add(new Wallet { WorkspaceId = Workspace });

        var refusal = await db.Awaiting(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        refusal.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.ConstraintName.Should().Be("IX_Wallets_WorkspaceId");
    }

    [PostgresFact]
    public async Task A_second_writer_of_one_balance_is_refused_rather_than_quietly_winning()
    {
        // Two contexts, because one context cannot stage this: EF would reuse the tracked entity and
        // the second write would carry the first one's stamp. Two connections is also what the real
        // thing is — the hourly pass in a scope of its own and an administrator's credit in a
        // request.
        var connectionString = await lane.FreshlyMigratedAsync("wallet_race");
        await SeedAsync(connectionString, 10_000);

        await using var tick = PostgresLane.Open(connectionString);
        await using var administrator = PostgresLane.Open(connectionString);

        var charged = await OnlyWalletAsync(tick);
        var credited = await OnlyWalletAsync(administrator);

        Move(charged, -2_000);
        await tick.SaveChangesAsync();

        Move(credited, +50_000);

        await administrator.Awaiting(c => c.SaveChangesAsync()).Should()
            .ThrowAsync<DbUpdateConcurrencyException>(
                "the stamp this context read has been rotated, so its UPDATE matches no row — and a " +
                "write that matched anyway would drop the charge that landed first");
    }

    [PostgresFact]
    public async Task The_writer_that_was_refused_can_reload_and_still_land_its_movement()
    {
        // The other half, and the half that decides whether the token is a safety feature or a way
        // to lose an administrator's credit. BillingTick.SaveAsync does exactly this: catch, reload,
        // re-apply the same movement on top of whatever the other writer left. Both movements have
        // to be in the balance at the end — 10,000 charged down to 8,000 and then credited to
        // 58,000 — because the point was never to refuse the second writer, only to stop it writing
        // over a number it had not seen.
        var connectionString = await lane.FreshlyMigratedAsync("wallet_reapply");
        await SeedAsync(connectionString, 10_000);

        await using var tick = PostgresLane.Open(connectionString);
        await using var administrator = PostgresLane.Open(connectionString);

        var charged = await OnlyWalletAsync(tick);
        var credited = await OnlyWalletAsync(administrator);

        Move(charged, -2_000);
        await tick.SaveChangesAsync();

        Move(credited, +50_000);
        try
        {
            await administrator.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await administrator.Entry(credited).ReloadAsync();
            Move(credited, +50_000);
            await administrator.SaveChangesAsync();
        }

        await using var reader = PostgresLane.Open(connectionString);
        (await reader.Wallets.AsNoTracking().SingleAsync(w => w.WorkspaceId == Workspace))
            .BalanceMinor.Should().Be(58_000, "neither movement may be lost to the other");
    }

    /// <summary>
    /// The balance movement and the stamp rotation, together — because separating them is the bug.
    /// A token nothing ever changes always matches, so both writers succeed and the second overwrites
    /// the first, which looks exactly like a working lock from the outside.
    /// </summary>
    private static void Move(Wallet wallet, long minor)
    {
        wallet.BalanceMinor += minor;
        wallet.ConcurrencyStamp = Guid.CreateVersion7();
    }

    private static async Task SeedAsync(string connectionString, long balanceMinor)
    {
        await using var db = PostgresLane.Open(connectionString);
        db.Wallets.Add(new Wallet { WorkspaceId = Workspace, BalanceMinor = balanceMinor });
        await db.SaveChangesAsync();
    }

    /// <summary>Tracked, deliberately: the token is checked against what this context read.</summary>
    private static Task<Wallet> OnlyWalletAsync(HarboraDbContext db) =>
        db.Wallets.SingleAsync(w => w.WorkspaceId == Workspace);
}
