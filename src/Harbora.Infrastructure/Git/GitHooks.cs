using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Git;

public enum GitHookState
{
    /// <summary>The provider listed a hook at Harbora's address, turned on, or has just made one.</summary>
    Confirmed = 0,

    /// <summary>The provider answered, and it has no working hook at Harbora's address.</summary>
    Missing = 1,

    /// <summary>Harbora cannot do this itself here at all: no connected account, no public address. Nothing was asked.</summary>
    NotPossible = 2,

    /// <summary>A provider was asked and refused, or could not be reached.</summary>
    Failed = 3
}

/// <summary>What is known about one repository's push webhook, as one sentence in each language.</summary>
/// <param name="Created">This call added the hook, rather than finding one there.</param>
public sealed record GitHookOutcome(GitHookState State, string Message, string MessageFa, bool Created = false)
{
    public bool IsConfirmed => State == GitHookState.Confirmed;
}

/// <summary>
/// The push webhook of a repository that belongs to a connected Git account, set up by Harbora through
/// the provider's API with the address and the secret it already has, so a push deploys with nothing
/// pasted into the provider.
///
/// <para>
/// There is no column for "the hook exists" and none was added: what the app page says comes from
/// asking the provider (<see cref="StatusAsync"/>), held for a few minutes, so it can only say a hook is
/// there when the provider just listed one, and it notices one deleted on the provider's side. A
/// repository on a <c>Custom</c> provider is never asked anything and says why.
/// </para>
/// </summary>
public interface IGitHookService
{
    /// <summary>The address a provider posts this repository's pushes to: the panel's public one. Null when it has none.</summary>
    string? AddressFor(Guid repositoryId);

    /// <summary>
    /// Finds the hook at <see cref="AddressFor"/> and sets it to the repository's current secret, or adds
    /// it when there is none: never a second one, so a retry, a second app on the repository or a second
    /// press all land on the one hook. Never throws; whatever stopped it is the outcome's sentence.
    /// </summary>
    Task<GitHookOutcome> EnsureAsync(Guid repositoryId, CancellationToken ct);

    /// <summary>What the provider says right now, or said within the last few minutes. Changes nothing. Never throws.</summary>
    Task<GitHookOutcome> StatusAsync(Guid repositoryId, CancellationToken ct);
}

/// <summary>
/// The last answer about each repository's hook, for a few minutes, so an app page drawn twice does not
/// ask a provider twice. One per panel process.
/// </summary>
public sealed class GitHookStatusCache
{
    public static readonly TimeSpan ConfirmedFor = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OtherwiseFor = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<Guid, (GitHookOutcome Outcome, DateTimeOffset Until)> _held = new();

    public GitHookOutcome? Get(Guid repositoryId, DateTimeOffset now) =>
        _held.TryGetValue(repositoryId, out var held) && held.Until > now ? held.Outcome : null;

    public void Set(Guid repositoryId, GitHookOutcome outcome, DateTimeOffset now) =>
        _held[repositoryId] = (outcome, now + (outcome.IsConfirmed ? ConfirmedFor : OtherwiseFor));

    public void Forget(Guid repositoryId) => _held.TryRemove(repositoryId, out _);
}

/// <inheritdoc />
public sealed class GitHookService(
    HarboraDbContext db,
    IGitProviderClient client,
    ISecretProtector protector,
    IAuditLogger audit,
    IConfiguration config,
    GitHookStatusCache cache,
    ISystemClock clock,
    ILogger<GitHookService> logger) : IGitHookService
{
    /// <summary>How long setting a hook up may take before it is reported as not done. It runs on a request.</summary>
    private static readonly TimeSpan EnsurePatience = TimeSpan.FromSeconds(15);

    /// <summary>How long a page waits to hear whether the hook is there before it says it could not ask.</summary>
    private static readonly TimeSpan StatusPatience = TimeSpan.FromSeconds(5);

    public string? AddressFor(Guid repositoryId) =>
        (config["PANEL_DOMAIN"] ?? "").Trim().TrimEnd('.', '/') is { Length: > 0 } panel
            ? $"https://{panel}/webhooks/git/{repositoryId}"
            : null;

    public async Task<GitHookOutcome> EnsureAsync(Guid repositoryId, CancellationToken ct)
    {
        var target = await TargetAsync(repositoryId, ct);
        if (target.Refusal is { } refusal)
        {
            cache.Forget(repositoryId);
            return refusal;
        }

        var (account, token, fullName, address, secret, _) = target;
        var spec = new HookSpec(address!, secret!);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(EnsurePatience);

        GitHookOutcome outcome;
        try
        {
            var existing = await FindAsync(account!, token!, fullName!, address!, deadline.Token);
            if (existing is null)
            {
                try
                {
                    var made = await client.CreateHookAsync(account!.Type, account.ApiBaseUrl, token!, fullName!, spec, deadline.Token);
                    await AuditAsync("git.webhook.created", account, repositoryId, fullName!, made.Id);
                    outcome = new GitHookOutcome(GitHookState.Confirmed,
                        $"Harbora added the webhook to {fullName} on {account.Type}: pushes deploy with nothing to paste.",
                        $"Harbora وب‌هوک را روی {fullName} در {account.Type} گذاشت: هر push بدون کپی کردن چیزی مستقر می‌شود.",
                        Created: true);
                    cache.Set(repositoryId, outcome, clock.UtcNow);
                    return outcome;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableEntity)
                {
                    // GitHub's answer to a second hook at one address ("Hook already exists"): one was
                    // added between the look and the add, by another request. That one is ours too.
                    existing = await FindAsync(account!, token!, fullName!, address!, deadline.Token);
                    if (existing is null) throw;
                }
            }

            await client.UpdateHookAsync(account!.Type, account.ApiBaseUrl, token!, fullName!, existing.Id, spec, deadline.Token);
            await AuditAsync("git.webhook.updated", account, repositoryId, fullName!, existing.Id);
            outcome = new GitHookOutcome(GitHookState.Confirmed,
                $"The webhook on {fullName} was already there; Harbora set its secret again, so pushes deploy with nothing to paste.",
                $"وب‌هوک روی {fullName} از قبل بود؛ Harbora سکرتش را دوباره گذاشت، پس هر push بدون کپی کردن چیزی مستقر می‌شود.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            outcome = Failure(ex, account!, fullName!, asking: false);
        }

        cache.Set(repositoryId, outcome, clock.UtcNow);
        return outcome;
    }

    public async Task<GitHookOutcome> StatusAsync(Guid repositoryId, CancellationToken ct)
    {
        if (cache.Get(repositoryId, clock.UtcNow) is { } held) return held;

        var target = await TargetAsync(repositoryId, ct);
        if (target.Refusal is { } refusal) return refusal;

        var (account, token, fullName, address, _, _) = target;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(StatusPatience);

        GitHookOutcome outcome;
        try
        {
            var hooks = await client.ListHooksAsync(account!.Type, account.ApiBaseUrl, token!, fullName!, deadline.Token);
            var ours = hooks.FirstOrDefault(h => SameAddress(h.Url, address!));
            outcome = ours switch
            {
                { Active: true } => new GitHookOutcome(GitHookState.Confirmed,
                    $"{account.Type} has Harbora's webhook on {fullName}, so pushes deploy with nothing to paste.",
                    $"{account.Type} وب‌هوک Harbora را روی {fullName} دارد، پس هر push بدون کپی کردن چیزی مستقر می‌شود."),
                not null => new GitHookOutcome(GitHookState.Missing,
                    $"Harbora's webhook on {fullName} is there but turned off on {account.Type}, so pushes do not deploy.",
                    $"وب‌هوک Harbora روی {fullName} هست ولی در {account.Type} خاموش است، پس pushها مستقر نمی‌شوند."),
                null => new GitHookOutcome(GitHookState.Missing,
                    $"{account.Type} has no webhook from Harbora on {fullName} yet, so pushes do not deploy.",
                    $"{account.Type} هنوز وب‌هوکی از Harbora روی {fullName} ندارد، پس pushها مستقر نمی‌شوند.")
            };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            outcome = Failure(ex, account!, fullName!, asking: true);
        }

        cache.Set(repositoryId, outcome, clock.UtcNow);
        return outcome;
    }

    // ---- shared --------------------------------------------------------------------------------

    private sealed record Target(
        GitProvider? Account, string? Token, string? FullName, string? Address, string? Secret, GitHookOutcome? Refusal = null)
    {
        public static Target Not(string message, string messageFa) =>
            new(null, null, null, null, null, new GitHookOutcome(GitHookState.NotPossible, message, messageFa));
    }

    /// <summary>
    /// Everything a provider has to be asked with, or why it cannot be asked at all. Unfiltered with no
    /// workspace to apply: the repository id is the whole scope, and every caller has already shown the
    /// repository is theirs (the app page, the Git page, the create that just made it).
    /// </summary>
    private async Task<Target> TargetAsync(Guid repositoryId, CancellationToken ct)
    {
        var repository = await db.GitRepositories.IgnoreQueryFilters().AsNoTracking().Include(r => r.Provider)
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repository is null)
            return Target.Not("This repository is no longer here.", "این مخزن دیگر اینجا نیست.");

        var account = repository.Provider;
        if (account is null || !GitAccounts.Connectable.Contains(account.Type))
            return Target.Not(
                "This repository was given by its address rather than through a connected Git account, so Harbora cannot add the webhook to it itself.",
                "این مخزن با آدرسش داده شده، نه از راه یک حساب Git وصل‌شده، پس Harbora نمی‌تواند وب‌هوک را خودش رویش بگذارد.");

        if (!GitRepositoryAddress.IsOn(account, repository.CloneUrl) || GitRepositoryAddress.FullNameOf(repository.CloneUrl) is not { } fullName)
            return Target.Not(
                $"This repository's address is not on {account.Name}, so that account cannot add a webhook to it.",
                $"آدرس این مخزن روی {account.Name} نیست، پس آن حساب نمی‌تواند رویش وب‌هوک بگذارد.");

        string? token = null;
        if (!string.IsNullOrEmpty(account.EncryptedCredential))
        {
            try { token = protector.Unprotect(account.EncryptedCredential); }
            catch (Exception ex)
            {
                logger.LogWarning("The token of Git account {ProviderId} could not be decrypted ({Kind}).", account.Id, ex.GetType().Name);
            }
        }
        if (string.IsNullOrEmpty(token))
            return Target.Not(
                $"The token stored for {account.Name} can no longer be read. Connect the account again on the Git page.",
                $"توکن {account.Name} دیگر خوانده نمی‌شود. حساب را در «اتصال Git» دوباره وصل کن.");

        if (AddressFor(repositoryId) is not { } address)
            return Target.Not(
                "This panel has no public address set (PANEL_DOMAIN), so a provider would have nowhere to send pushes.",
                "برای این پنل آدرس عمومی (PANEL_DOMAIN) تنظیم نشده، پس سرویس گیت جایی برای فرستادن pushها ندارد.");

        return new Target(account, token, fullName, address, repository.WebhookSecret);
    }

    private async Task<RemoteHook?> FindAsync(GitProvider account, string token, string fullName, string address, CancellationToken ct) =>
        (await client.ListHooksAsync(account.Type, account.ApiBaseUrl, token, fullName, ct))
        .FirstOrDefault(h => SameAddress(h.Url, address));

    private static bool SameAddress(string url, string address) =>
        string.Equals(url.Trim().TrimEnd('/'), address.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The refusal in a sentence. A status code says which, and the token is in no exception's message.</summary>
    private GitHookOutcome Failure(Exception ex, GitProvider account, string fullName, bool asking)
    {
        var status = (ex as HttpRequestException)?.StatusCode;
        logger.LogWarning("The webhook of {Repository} on {Provider} could not be {Done}: {Status} {Reason}",
            fullName, account.Type, asking ? "read" : "set up", status, ex.Message);

        return status switch
        {
            HttpStatusCode.Unauthorized => new GitHookOutcome(GitHookState.Failed,
                $"{account.Type} refused {account.Name}'s token: it may have expired or been revoked. Connect the account again on the Git page.",
                $"{account.Type} توکن {account.Name} را نپذیرفت؛ شاید منقضی یا باطل شده باشد. حساب را در «اتصال Git» دوباره وصل کن."),
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound => new GitHookOutcome(GitHookState.Failed,
                $"{account.Name} may not manage webhooks on {fullName}. That takes admin rights on the repository (Maintainer on GitLab), and on GitHub a token with the repo or admin:repo_hook scope.",
                $"{account.Name} اجازهٔ مدیریت وب‌هوک‌های {fullName} را ندارد. این کار دسترسی ادمین روی مخزن می‌خواهد (روی GitLab نقش Maintainer)، و روی GitHub توکنی با دسترسی repo یا admin:repo_hook."),
            { } other => new GitHookOutcome(GitHookState.Failed,
                $"{account.Type} answered {(int)other} when asked about the webhook on {fullName}.",
                $"{account.Type} وقتی دربارهٔ وب‌هوک {fullName} پرسیده شد، جواب {(int)other} داد."),
            null => new GitHookOutcome(GitHookState.Failed,
                asking
                    ? $"{account.Type} could not be asked about the webhook on {fullName} just now."
                    : $"{account.Type} could not be reached to set up the webhook on {fullName}.",
                asking
                    ? $"الان نشد از {account.Type} دربارهٔ وب‌هوک {fullName} بپرسم."
                    : $"برای گذاشتن وب‌هوک روی {fullName} به {account.Type} نرسیدم.")
        };
    }

    private Task AuditAsync(string action, GitProvider account, Guid repositoryId, string fullName, string hookId) =>
        audit.LogAsync(action, "GitRepository", repositoryId.ToString(),
            metadataJson: JsonSerializer.Serialize(new { repository = fullName, provider = account.Type.ToString(), hookId }),
            workspaceId: account.WorkspaceId, ct: CancellationToken.None);
}
