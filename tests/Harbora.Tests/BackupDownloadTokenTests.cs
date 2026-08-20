using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project 10: the token a self-serve database export's temporary download link is minted from.
/// Same four rules as <see cref="VolumeDownloadTokenTests"/> (D4) — single use, self-expiry borrowed
/// from <c>AdminerSession</c>, the backup fixed at mint time, and a workspace pairing resolved through
/// the tenant filter before the row is ever written — retargeted at a <see cref="Backup"/> artifact.
/// </summary>
public sealed class BackupDownloadTokenTests : IDisposable
{
    private readonly DbContextOptions<HarboraDbContext> _options = new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("backup-download-tokens-" + Guid.NewGuid())
        .Options;

    private readonly HarboraDbContext _db;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    public BackupDownloadTokenTests()
    {
        _db = new HarboraDbContext(_options, SystemWorkspaceScope.Instance);
    }

    private BackupDownloadTokens Service(HarboraDbContext? db = null) => new(db ?? _db, _clock);

    private Backup SeedCompletedBackup(HarboraDbContext db, Guid workspaceId)
    {
        var destination = new BackupDestination
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Name = "local", Type = BackupDestinationType.Local
        };
        var backup = new Backup
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, DestinationId = destination.Id,
            Type = BackupType.Database, Status = BackupStatus.Completed,
            TargetRef = Guid.NewGuid().ToString(), ArtifactPath = "/tmp/export.sql.gz",
            ExpiresAt = _clock.UtcNow + DatabaseExportPlan.ArtifactLifetime
        };
        db.BackupDestinations.Add(destination);
        db.Backups.Add(backup);
        db.SaveChanges();
        return backup;
    }

    // ---- a successful mint, and what it stores -------------------------------------------------

    [Fact]
    public async Task A_minted_token_stores_only_a_hash_of_it()
    {
        var backup = SeedCompletedBackup(_db, Guid.CreateVersion7());

        var mint = await Service().MintAsync(backup, default);

        var stored = await _db.BackupDownloadTokens.SingleAsync();
        stored.TokenHash.Should().NotBe(mint.Token,
            "a database dump must not be a list of working download links");
        stored.TokenHash.Should().HaveLength(64, "SHA-256, hex-encoded");
        stored.BackupId.Should().Be(backup.Id);
    }

    [Fact]
    public async Task A_mints_expiry_is_the_borrowed_admin_session_lifetime_and_nothing_else()
    {
        var backup = SeedCompletedBackup(_db, Guid.CreateVersion7());

        var mint = await Service().MintAsync(backup, default);
        var stored = await _db.BackupDownloadTokens.SingleAsync();

        mint.ExpiresAt.Should().Be(stored.CreatedAt + AdminerSession.Lifetime,
            "the link's own span comes from AdminerSession.Lifetime, not a second value invented here " +
            "— that is separate from how long the artifact itself is kept (DatabaseExportPlan.ArtifactLifetime)");
    }

    // ---- rule 1: single use ---------------------------------------------------------------------

    [Fact]
    public async Task Minting_then_redeeming_names_exactly_the_backup_it_was_minted_for()
    {
        var backup = SeedCompletedBackup(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(backup, default);

        var redemption = await Service().RedeemAsync(mint.Token, default);

        redemption.Ok.Should().BeTrue();
        redemption.BackupId.Should().Be(backup.Id);
    }

    [Fact]
    public async Task A_token_can_be_redeemed_exactly_once()
    {
        var backup = SeedCompletedBackup(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(backup, default);

        var first = await Service().RedeemAsync(mint.Token, default);
        var second = await Service().RedeemAsync(mint.Token, default);

        first.Ok.Should().BeTrue("the first redemption is the whole point of minting it");
        second.Ok.Should().BeFalse(
            "a shareable link can be forwarded, and 'used once' is what bounds where it ends up");
    }

    [Fact]
    public async Task Redeeming_marks_the_row_spent_so_a_second_attempt_has_something_to_check_against()
    {
        var backup = SeedCompletedBackup(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(backup, default);

        await Service().RedeemAsync(mint.Token, default);

        (await _db.BackupDownloadTokens.SingleAsync()).UsedAt.Should().NotBeNull();
    }

    // ---- rule 2: expires on its own ------------------------------------------------------------

    [Fact]
    public async Task A_token_past_its_hour_is_refused_even_though_it_was_never_used()
    {
        var backup = SeedCompletedBackup(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(backup, default);

        _clock.UtcNow += AdminerSession.Lifetime + TimeSpan.FromSeconds(1);

        var redemption = await Service().RedeemAsync(mint.Token, default);

        redemption.Ok.Should().BeFalse("past the lifetime it is refused whether or not it was used");
    }

    [Fact]
    public async Task A_token_one_second_short_of_its_hour_still_works()
    {
        var backup = SeedCompletedBackup(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(backup, default);

        _clock.UtcNow += AdminerSession.Lifetime - TimeSpan.FromSeconds(1);

        var redemption = await Service().RedeemAsync(mint.Token, default);

        redemption.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_token_is_refused()
    {
        var redemption = await Service().RedeemAsync("not-a-real-token", default);

        redemption.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task A_token_for_a_backup_that_is_no_longer_completed_is_refused()
    {
        // The backup this token names can stop being redeemable out from under it — a retry that
        // re-runs the export, or the artifact simply failing to survive — and a stale token must not
        // hand out whatever is left.
        var backup = SeedCompletedBackup(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(backup, default);

        backup.Status = BackupStatus.Failed;
        await _db.SaveChangesAsync();

        var redemption = await Service().RedeemAsync(mint.Token, default);

        redemption.Ok.Should().BeFalse();
    }

    // ---- rule 3: the backup is fixed at mint time ------------------------------------------------

    [Fact]
    public async Task Redemption_takes_no_backup_id_at_all_so_nothing_about_it_can_be_varied()
    {
        // RedeemAsync's own signature is the guarantee: it accepts only the token. There is no
        // parameter here a caller could use to ask for a different backup than the one minted.
        var backup = SeedCompletedBackup(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(backup, default);

        var redemption = await Service().RedeemAsync(mint.Token, default);

        redemption.BackupId.Should().Be(backup.Id);
    }

    // ---- rule 4: it belongs to one workspace, resolved through the tenant filter at mint time -----

    [Fact]
    public async Task Redemption_finds_the_backup_even_though_the_redeeming_request_has_no_workspace_in_scope()
    {
        var workspaceId = Guid.CreateVersion7();
        using var mintingDb = new HarboraDbContext(_options, new FixedWorkspaceScope(workspaceId));
        var backup = SeedCompletedBackup(mintingDb, workspaceId);

        var mint = await Service(mintingDb).MintAsync(backup, default);

        using var redeemingDb = new HarboraDbContext(_options, new FixedWorkspaceScope(Guid.Empty));
        var redemption = await Service(redeemingDb).RedeemAsync(mint.Token, default);

        redemption.Ok.Should().BeTrue(
            "the backup's ownership was already proved once, through the tenant filter, at mint time");
        redemption.BackupId.Should().Be(backup.Id);
    }

    // ---- the sweep ------------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_retires_spent_and_expired_tokens_and_leaves_a_live_one()
    {
        var workspaceId = Guid.CreateVersion7();

        var spentBackup = SeedCompletedBackup(_db, workspaceId);
        var spent = await Service().MintAsync(spentBackup, default);
        await Service().RedeemAsync(spent.Token, default);

        var expiredBackup = SeedCompletedBackup(_db, workspaceId);
        var expired = await Service().MintAsync(expiredBackup, default);
        var expiredRow = await _db.BackupDownloadTokens.SingleAsync(t => t.BackupId == expiredBackup.Id);
        expiredRow.CreatedAt -= AdminerSession.Lifetime + TimeSpan.FromMinutes(1);
        await _db.SaveChangesAsync();
        _ = expired;

        var liveBackup = SeedCompletedBackup(_db, workspaceId);
        var live = await Service().MintAsync(liveBackup, default);

        var closed = await Service().SweepAsync(default);

        closed.Should().Be(2, "the spent row and the expired row, and no more");
        var remaining = await _db.BackupDownloadTokens.Select(t => t.BackupId).ToListAsync();
        remaining.Should().ContainSingle().Which.Should().Be(liveBackup.Id);
        _ = live;
    }

    public void Dispose() => _db.Dispose();
}
