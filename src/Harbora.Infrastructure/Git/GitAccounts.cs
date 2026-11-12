using System.Net;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Git;

/// <summary>
/// One connected account's repositories as a picker shows them, or the one sentence that says why
/// they could not be listed. Never a token.
/// </summary>
/// <param name="Truncated">The list stopped at <see cref="IGitProviderClient.MaxRepositories"/>; there may be more.</param>
/// <param name="Problem">Why nothing is listed, in a sentence. Null when the provider answered.</param>
public sealed record AccountRepositories(
    Guid ProviderId,
    string Name,
    GitProviderType Type,
    IReadOnlyList<RemoteRepository> Repositories,
    bool Truncated = false,
    string? Problem = null,
    string? ProblemFa = null);

/// <summary>
/// The workspace's connected Git accounts: a GitHub, GitLab or Gitea provider row that holds a token.
/// A <c>Custom</c> provider, the private one every app made from a pasted address gets, is not one:
/// there is no API behind it to ask anything of.
///
/// <para>
/// Every read is unfiltered with the workspace applied here, because one caller is the launch's
/// inspection job, which has no session: under the tenant filter it would find no account, read the
/// repository without one, and report nothing wrong.
/// </para>
/// </summary>
public sealed class GitAccounts(
    HarboraDbContext db,
    IGitProviderClient client,
    ISecretProtector protector,
    ILogger<GitAccounts> logger)
{
    /// <summary>The provider types with an API behind them that <see cref="IGitProviderClient"/> speaks.</summary>
    public static readonly IReadOnlyList<GitProviderType> Connectable =
        [GitProviderType.GitHub, GitProviderType.GitLab, GitProviderType.Gitea];

    /// <summary>How long a page waits on a provider before it says so and draws without the list.</summary>
    public static readonly TimeSpan PagePatience = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<GitProvider>> ConnectedAsync(Guid workspaceId, CancellationToken ct)
    {
        var types = Connectable.ToList();
        return await db.GitProviders.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && types.Contains(p.Type)
                        && p.EncryptedCredential != null && p.EncryptedCredential != "")
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>This workspace's account with this id, while it is still connected.</summary>
    public async Task<GitProvider?> FindAsync(Guid workspaceId, Guid providerId, CancellationToken ct) =>
        (await ConnectedAsync(workspaceId, ct)).FirstOrDefault(p => p.Id == providerId);

    /// <summary>The account's token, or null when it cannot be decrypted (a master key rotated since it was stored).</summary>
    public string? TokenOf(GitProvider account)
    {
        if (string.IsNullOrEmpty(account.EncryptedCredential)) return null;
        try { return protector.Unprotect(account.EncryptedCredential); }
        catch (Exception ex)
        {
            logger.LogWarning("The token of Git account {ProviderId} could not be decrypted ({Kind}).", account.Id, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>
    /// Every connected account's repositories, asked of each provider at once and each given
    /// <paramref name="patience"/>. One account that cannot answer is one sentence beside the others.
    /// </summary>
    public async Task<IReadOnlyList<AccountRepositories>> ListAsync(Guid workspaceId, TimeSpan patience, CancellationToken ct)
    {
        var accounts = await ConnectedAsync(workspaceId, ct);
        return await Task.WhenAll(accounts.Select(account => ListOneAsync(account, patience, ct)));
    }

    private async Task<AccountRepositories> ListOneAsync(GitProvider account, TimeSpan patience, CancellationToken ct)
    {
        var token = TokenOf(account);
        if (token is null)
            return Unlisted(account,
                "The token stored for this account can no longer be read. Connect it again on the Git page.",
                "توکن این حساب دیگر خوانده نمی‌شود. آن را در «اتصال Git» دوباره وصل کن.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(patience);
        try
        {
            var repositories = await client.ListRepositoriesAsync(account.Type, account.ApiBaseUrl, token, deadline.Token);
            return new AccountRepositories(account.Id, account.Name, account.Type, repositories,
                Truncated: repositories.Count >= IGitProviderClient.MaxRepositories);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unlisted(account,
                $"{account.Type} did not answer in time, so your repositories are not listed. Paste the address instead.",
                $"{account.Type} به‌موقع جواب نداد و مخزن‌هایت فهرست نشد. آدرس را بنویس.");
        }
        catch (Exception ex) when (Refused(ex))
        {
            return Unlisted(account,
                $"{account.Type} refused this account's token: it may have expired or been revoked. Connect it again on the Git page, or paste the address.",
                $"{account.Type} توکن این حساب را نپذیرفت؛ شاید منقضی یا باطل شده باشد. آن را در «اتصال Git» دوباره وصل کن، یا آدرس را بنویس.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message of an HttpRequestException carries a status or a socket error, never the
            // token, which only ever travels in a header.
            logger.LogWarning("Listing the repositories of Git account {ProviderId} failed: {Reason}", account.Id, ex.Message);
            return Unlisted(account,
                $"Your repositories could not be fetched from {account.Type} just now. Paste the address instead.",
                $"الان نشد مخزن‌هایت را از {account.Type} بگیرم. آدرس را بنویس.");
        }
    }

    /// <summary>
    /// The connected account whose own list holds the repository at <paramref name="cloneUrl"/>, with its
    /// token: what makes a pasted address "plainly this account's". Only accounts on the address's own
    /// host are asked, so no token is ever sent anywhere it does not belong. Asking costs a list per
    /// account, which is why only the inspection job does it; an account that cannot answer is skipped,
    /// and the repository is read the way a pasted address always was.
    /// </summary>
    public async Task<(GitProvider Account, string Token)?> OwnerOfAsync(
        Guid workspaceId, string cloneUrl, TimeSpan patience, CancellationToken ct)
    {
        var wanted = GitRepositoryAddress.Key(cloneUrl);
        if (wanted is null) return null;

        foreach (var account in (await ConnectedAsync(workspaceId, ct)).Where(a => GitRepositoryAddress.IsOn(a, cloneUrl)))
        {
            if (TokenOf(account) is not { } token) continue;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(patience);
            try
            {
                var repositories = await client.ListRepositoriesAsync(account.Type, account.ApiBaseUrl, token, deadline.Token);
                if (repositories.Any(r => GitRepositoryAddress.Key(r.CloneUrl) == wanted))
                    return (account, token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Git account {ProviderId} could not be asked about a pasted address: {Reason}", account.Id, ex.Message);
            }
        }

        return null;
    }

    private static bool Refused(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    private static AccountRepositories Unlisted(GitProvider account, string problem, string problemFa) =>
        new(account.Id, account.Name, account.Type, [], Problem: problem, ProblemFa: problemFa);
}
