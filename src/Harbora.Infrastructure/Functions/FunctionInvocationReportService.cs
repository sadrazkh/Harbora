using System.Security.Cryptography;
using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// Verifies a generated host's own report of a public call against the app it claims to come from and
/// writes the <see cref="FunctionInvocation"/> row the panel could never write itself — see this
/// class's own interface doc for the decision this reverses.
///
/// <para>
/// Authenticity is <see cref="App.FunctionInvokeSecret"/> in the same "the other direction" shape
/// <see cref="CustomEventIngestService"/> already established: the same secret the generated host
/// already checks when the panel calls in (<see cref="FunctionProject.SecretHeader"/> /
/// <see cref="FunctionProject.SecretEnvVar"/>), compared here the other way round. An app only ever
/// knows its own secret, so an app id in the URL with the wrong secret — or a workspace's own secret
/// against another workspace's app id — both fail the same comparison, before
/// <see cref="App.WorkspaceId"/> is ever trusted for anything.
/// </para>
/// </summary>
public sealed class FunctionInvocationReportService(
    HarboraDbContext db, ISecretProtector protector, ILogger<FunctionInvocationReportService> logger)
    : IFunctionInvocationReportService
{
    /// <summary>Same ceiling <c>FunctionInvoker.CompleteAsync</c> truncates a failure reason to —
    /// the column is sized the same way (<c>HasMaxLength(1000)</c>) for both origins.</summary>
    private const int MaxErrorLength = 900;

    public async Task<FunctionInvocationReportOutcome> ReportAsync(
        Guid appId, string? providedSecret, FunctionInvocationReportRequest request, CancellationToken ct)
    {
        // Anonymous door, same trap as CustomEventIngestService: no session means the tenant filter
        // hides every row, including the one whose own secret is about to prove it. The scope is
        // pinned by the app id in the URL instead.
        var app = await db.Apps.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == appId && a.SourceType == AppSourceType.InlineCode, ct);
        if (app is null) return FunctionInvocationReportOutcome.Unauthorized;

        var secret = SafeUnprotect(app.FunctionInvokeSecret);
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(providedSecret) ||
            !FixedEquals(providedSecret, secret))
        {
            logger.LogWarning("Rejected a public-call report for app {App}: bad or missing invoke secret.", appId);
            return FunctionInvocationReportOutcome.Unauthorized;
        }

        if (string.IsNullOrWhiteSpace(request.Slug))
            return FunctionInvocationReportOutcome.UnknownFunction;

        var fn = await db.FunctionDefinitions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.AppId == app.Id && f.Slug == request.Slug, ct);
        if (fn is null) return FunctionInvocationReportOutcome.UnknownFunction;

        // The host measured its own elapsed time; this only clamps a negative or absent value rather
        // than trusting it blindly, the way FunctionInvoker.CompleteAsync trusts its own stopwatch.
        var duration = Math.Max(0, request.DurationMs ?? 0);
        var completedAt = DateTimeOffset.UtcNow;
        var error = request.Error is { Length: > MaxErrorLength } e ? e[..MaxErrorLength] : request.Error;

        db.FunctionInvocations.Add(new FunctionInvocation
        {
            FunctionId = fn.Id,
            AppId = app.Id,
            WorkspaceId = app.WorkspaceId,
            Trigger = FunctionTrigger.Http,
            // The one fact this whole door exists to record honestly: nobody here watched this call
            // happen, only the host's own account of it, after the fact.
            Origin = FunctionInvocationOrigin.PublicCall,
            StartedAt = completedAt - TimeSpan.FromMilliseconds(duration),
            CompletedAt = completedAt,
            DurationMs = duration,
            StatusCode = request.StatusCode,
            Succeeded = request.StatusCode is { } sc && sc < 400,
            Error = error
        });
        await db.SaveChangesAsync(ct);

        return FunctionInvocationReportOutcome.Accepted;
    }

    private string? SafeUnprotect(string? ciphertext)
    {
        if (string.IsNullOrEmpty(ciphertext)) return null;
        try { return protector.Unprotect(ciphertext); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A function app's invoke secret could not be decrypted.");
            return null;
        }
    }

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
