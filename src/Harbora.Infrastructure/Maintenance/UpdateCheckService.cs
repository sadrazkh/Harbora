using System.Net.Http.Json;
using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// Once a day, asks GitHub whether a newer Harbora has been released, and records the tag for the
/// dashboard to mention.
///
/// **Off unless an operator turns it on.** It is an outbound request to a third party from the
/// server; a check nobody asked for is telemetry, and the same principle governs registry
/// discovery. It never updates anything — the install is a deliberate act on the server. What this
/// produces is a single stored tag and, from it, one Info banner: "0.3.0 is out, you run 0.2.0".
/// </summary>
public sealed class UpdateCheckService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpFactory,
    ILogger<UpdateCheckService> logger) : BackgroundService
{
    private const string ReleasesApi = "https://api.github.com/repos/sadrazkh/Harbora/releases/latest";
    private static readonly TimeSpan Tick = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await CheckAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Update check failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One check. Public so it can be run on demand rather than waiting a day.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var enabled = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.UpdateCheckEnabled)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)) return;

        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        // GitHub refuses anonymous requests with no user agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Harbora-update-check");

        GitHubRelease? release;
        try
        {
            release = await client.GetFromJsonAsync<GitHubRelease>(ReleasesApi, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // A rate limit, an outage, a redesigned payload — none is worth an error. The banner
            // simply does not update, which is the honest outcome of "we could not check".
            logger.LogWarning(e, "Could not reach GitHub to check for updates.");
            return;
        }

        var tag = release?.TagName;
        if (string.IsNullOrWhiteSpace(tag)) return;

        // Stored whatever it is; the dashboard decides whether it is actually newer than the running
        // build, so the comparison rule has one home.
        await UpsertAsync(db, SettingKeys.UpdateLatestTag, tag.Trim(), ct);
        logger.LogInformation("Latest published Harbora release is {Tag}.", tag);
    }

    private static async Task UpsertAsync(HarboraDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.Settings.Add(new Setting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync(ct);
    }

    private sealed record GitHubRelease(
        [property: System.Text.Json.Serialization.JsonPropertyName("tag_name")] string? TagName);
}
