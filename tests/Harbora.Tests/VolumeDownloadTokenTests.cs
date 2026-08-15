using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Storage;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Sub-project D4, Task 1: the token a temporary volume download link is minted from, and the four
/// rules that make handing one to somebody with no panel session acceptable — single use, self-expiry
/// borrowed from <c>AdminerSession</c>, a path fixed at mint time, and an app/volume pairing resolved
/// through the tenant filter before the row is ever written.
/// </summary>
public sealed class VolumeDownloadTokenTests : IDisposable
{
    private readonly DbContextOptions<HarboraDbContext> _options = new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("volume-download-tokens-" + Guid.NewGuid())
        .Options;

    private readonly HarboraDbContext _db;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));

    public VolumeDownloadTokenTests()
    {
        _db = new HarboraDbContext(_options, SystemWorkspaceScope.Instance);
    }

    private VolumeDownloadTokens Service(HarboraDbContext? db = null) => new(db ?? _db, _clock);

    private (App App, Volume Volume) SeedAppWithVolume(
        HarboraDbContext db, Guid workspaceId, Guid? serverId = null)
    {
        var app = new App
        {
            WorkspaceId = workspaceId,
            ServerId = serverId ?? Guid.CreateVersion7(),
            Name = "app-" + Guid.NewGuid().ToString("N")[..8],
            Slug = "app-" + Guid.NewGuid().ToString("N")[..8],
            SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/seeded:1.0",
            Status = AppStatus.Running
        };
        var volume = new Volume
        {
            AppId = app.Id,
            Name = "vol-" + Guid.NewGuid().ToString("N")[..8],
            MountPath = "/data"
        };

        db.Apps.Add(app);
        db.Volumes.Add(volume);
        db.SaveChanges();

        return (app, volume);
    }

    // ---- a successful mint, and what it stores -------------------------------------------------

    [Fact]
    public async Task A_minted_token_stores_only_a_hash_of_it()
    {
        var (app, volume) = SeedAppWithVolume(_db, Guid.CreateVersion7());

        var mint = await Service().MintAsync(app, volume, "reports/2026-08.csv", default);

        var stored = await _db.VolumeDownloadTokens.SingleAsync();
        stored.TokenHash.Should().NotBe(mint.Token,
            "a database dump must not be a list of working download links");
        stored.TokenHash.Should().HaveLength(64, "SHA-256, hex-encoded");
        stored.Path.Should().Be("reports/2026-08.csv");
    }

    [Fact]
    public async Task A_mints_expiry_is_the_borrowed_admin_session_lifetime_and_nothing_else()
    {
        var (app, volume) = SeedAppWithVolume(_db, Guid.CreateVersion7());

        var mint = await Service().MintAsync(app, volume, "db.sql", default);
        var stored = await _db.VolumeDownloadTokens.SingleAsync();

        mint.ExpiresAt.Should().Be(stored.CreatedAt + AdminerSession.Lifetime,
            "the span comes from AdminerSession.Lifetime, not a second value invented for this feature");
    }

    // ---- rule 1: single use ---------------------------------------------------------------------

    [Fact]
    public async Task Minting_then_redeeming_serves_exactly_the_file_it_was_minted_for()
    {
        var (app, volume) = SeedAppWithVolume(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(app, volume, "backups/db.sql", default);

        var redemption = await Service().RedeemAsync(mint.Token, default);

        redemption.Ok.Should().BeTrue();
        redemption.ServerId.Should().Be(app.ServerId);
        redemption.VolumeName.Should().Be(volume.Name);
        redemption.Path.Should().Be("backups/db.sql");
    }

    [Fact]
    public async Task A_token_can_be_redeemed_exactly_once()
    {
        var (app, volume) = SeedAppWithVolume(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(app, volume, "db.sql", default);

        var first = await Service().RedeemAsync(mint.Token, default);
        var second = await Service().RedeemAsync(mint.Token, default);

        first.Ok.Should().BeTrue("the first redemption is the whole point of minting it");
        second.Ok.Should().BeFalse(
            "a shareable link can be forwarded, and 'used once' is what bounds where it ends up");
    }

    [Fact]
    public async Task Redeeming_marks_the_row_spent_so_a_second_attempt_has_something_to_check_against()
    {
        var (app, volume) = SeedAppWithVolume(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(app, volume, "db.sql", default);

        await Service().RedeemAsync(mint.Token, default);

        (await _db.VolumeDownloadTokens.SingleAsync()).UsedAt.Should().NotBeNull();
    }

    // ---- rule 2: expires on its own ------------------------------------------------------------

    [Fact]
    public async Task A_token_past_its_hour_is_refused_even_though_it_was_never_used()
    {
        var (app, volume) = SeedAppWithVolume(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(app, volume, "db.sql", default);

        _clock.UtcNow += AdminerSession.Lifetime + TimeSpan.FromSeconds(1);

        var redemption = await Service().RedeemAsync(mint.Token, default);

        redemption.Ok.Should().BeFalse("past the lifetime it is refused whether or not it was used");
    }

    [Fact]
    public async Task A_token_one_second_short_of_its_hour_still_works()
    {
        // The other half of the same boundary: a rule that refuses too early is as wrong as one that
        // refuses too late.
        var (app, volume) = SeedAppWithVolume(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(app, volume, "db.sql", default);

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

    // ---- rule 3: the path is fixed at mint time --------------------------------------------------

    [Fact]
    public async Task Redemption_takes_no_path_at_all_so_nothing_about_it_can_be_varied()
    {
        // RedeemAsync's own signature is the guarantee: it accepts only the token. There is no
        // parameter here a caller could use to ask for a different file than the one minted — the
        // volume-path defect this project has already fixed twice was exactly a path that could vary.
        var (app, volume) = SeedAppWithVolume(_db, Guid.CreateVersion7());
        var mint = await Service().MintAsync(app, volume, "one/exact/file.txt", default);

        var redemption = await Service().RedeemAsync(mint.Token, default);

        redemption.Path.Should().Be("one/exact/file.txt");
    }

    // ---- rule 4: it belongs to one app, resolved through the tenant filter at mint time ----------

    [Fact]
    public async Task Redemption_finds_the_app_even_though_the_redeeming_request_has_no_workspace_in_scope()
    {
        // Mint happens the way AppDataController does it: through a context scoped to the caller's
        // own workspace. Redeem happens the way the real route does it: through a context an
        // unauthenticated request resolves to Guid.Empty (HttpWorkspaceScope), which matches no
        // tenant's App row under the ordinary filter. Both contexts share one store.
        var workspaceId = Guid.CreateVersion7();
        using var mintingDb = new HarboraDbContext(_options, new FixedWorkspaceScope(workspaceId));
        var (app, volume) = SeedAppWithVolume(mintingDb, workspaceId);

        var mint = await Service(mintingDb).MintAsync(app, volume, "db.sql", default);

        using var redeemingDb = new HarboraDbContext(_options, new FixedWorkspaceScope(Guid.Empty));
        var redemption = await Service(redeemingDb).RedeemAsync(mint.Token, default);

        redemption.Ok.Should().BeTrue(
            "the app/volume pairing was already proved once, through the tenant filter, at mint time");
        redemption.ServerId.Should().Be(app.ServerId);
    }

    // ---- the sweeper ------------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_retires_spent_and_expired_tokens_and_leaves_a_live_one()
    {
        var (app, volume) = SeedAppWithVolume(_db, Guid.CreateVersion7());

        var spent = await Service().MintAsync(app, volume, "spent.txt", default);
        await Service().RedeemAsync(spent.Token, default);

        var expired = await Service().MintAsync(app, volume, "expired.txt", default);
        var expiredRow = await _db.VolumeDownloadTokens.SingleAsync(t => t.Path == "expired.txt");
        expiredRow.CreatedAt -= AdminerSession.Lifetime + TimeSpan.FromMinutes(1);
        await _db.SaveChangesAsync();
        _ = expired; // the raw token is not needed again; only its row's age matters to the sweep

        var live = await Service().MintAsync(app, volume, "live.txt", default);

        var closed = await Service().SweepAsync(default);

        closed.Should().Be(2, "the spent row and the expired row, and no more");
        var remaining = await _db.VolumeDownloadTokens.Select(t => t.Path).ToListAsync();
        remaining.Should().ContainSingle().Which.Should().Be("live.txt");
        _ = live;
    }

    public void Dispose() => _db.Dispose();
}
