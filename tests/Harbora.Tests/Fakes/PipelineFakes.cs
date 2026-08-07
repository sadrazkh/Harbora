using System.Net;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Records the status transitions the pipeline published, in order.
///
/// <para>
/// Both methods refuse a cancelled token, because the real one does:
/// <c>SignalRDeploymentLogStream</c> hands it straight to <c>IHubClients.SendAsync</c>, which
/// throws. A fake that published anyway could not show the guarantee this stream exists for — that
/// what a person is watching keeps being told the truth after the clock has killed the job — and
/// for a while it did not, which is how a deadline came to end a deployment in silence.
/// </para>
/// </summary>
public sealed class RecordingLogStream : IDeploymentLogStream
{
    private readonly List<string> _lines = [];
    private readonly List<DeploymentStatus> _statuses = [];
    private readonly object _gate = new();

    public IReadOnlyList<DeploymentStatus> Statuses { get { lock (_gate) return _statuses.ToList(); } }
    public IReadOnlyList<string> Lines { get { lock (_gate) return _lines.ToList(); } }

    public Task PublishLogAsync(Guid deploymentId, LogStream stream, string line, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) _lines.Add(line);
        return Task.CompletedTask;
    }

    public Task PublishStatusAsync(Guid deploymentId, DeploymentStatus status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) _statuses.Add(status);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Records what the proxy was asked to route to, so cutover can be asserted.
///
/// <para>
/// An apply takes no routes — the real engine reads the platform's own — so this reads them the same
/// way, from the stored rows at the moment of the call. That is what lets a test see the config a
/// deployment published rather than the argument it passed, which is where the multi-tenant outage
/// hid: the argument looked right to the caller that wrote it.
/// </para>
/// </summary>
public sealed class RecordingProxyEngine(Func<IReadOnlyList<Route>> storedRoutes) : IProxyEngine
{
    public sealed record Applied(string Host, string TargetService, int TargetPort);

    public List<Applied> Applications { get; } = [];

    /// <summary>
    /// What the <i>last</i> apply published, rather than everything every apply ever published. The
    /// real engine rewrites one file, so the latest apply is what is serving; a cumulative list
    /// cannot answer "what is live now" once a deployment applies more than once — which the failure
    /// path does, to put the routing back where it found it.
    /// </summary>
    public IReadOnlyList<Applied> Live { get; private set; } = [];

    public int ApplyCount { get; private set; }
    public ProxyApplyResult Result { get; set; } = new(true, null, false);

    public ProxyConfigPreview Preview(IReadOnlyList<Route> routes) => new("yaml", string.Empty);
    public ProxyValidationResult Validate(IReadOnlyList<Route> routes) => new(true, [], []);

    public Task<ProxyApplyResult> ApplyAllAsync(Guid? callerWorkspaceId, CancellationToken ct)
    {
        ApplyCount++;
        var applied = storedRoutes()
            .Where(r => r.IsEnabled)
            .Select(r => new Applied(r.Host, r.TargetService ?? "", r.TargetPort))
            .ToList();
        Live = applied;
        Applications.AddRange(applied);
        return Task.FromResult(Result);
    }
}

/// <summary>Git service that never touches the network; hands back a fixed checkout.</summary>
public sealed class FakeGitService(string localPath) : IGitService
{
    public int CheckoutCount { get; private set; }

    public Task<GitCheckout> CheckoutAsync(string cloneUrl, string gitRef, string? credentialToken,
        string workingDir, IProgress<string> log, CancellationToken ct)
    {
        CheckoutCount++;
        log.Report($"checked out {gitRef}");
        return Task.FromResult(new GitCheckout("abc1234", "test commit", "tester", localPath));
    }

    public Task<IReadOnlyList<GitRef>> ListRefsAsync(string cloneUrl, string? credentialToken, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<GitRef>>([]);
}

/// <summary>Identity protector — tests assert on redaction separately (SecurityTests).</summary>
public sealed class PassthroughProtector : ISecretProtector
{
    /// <summary>
    /// Randomised on purpose. The real protector uses a fresh nonce per call, and a deterministic
    /// fake hid a production bug: the backup engine derived its archive key from Protect() output,
    /// which round-tripped fine here and was unreproducible in production.
    /// </summary>
    public string Protect(string plaintext) =>
        plaintext + "|nonce:" + Guid.NewGuid().ToString("N")[..8];

    public string Unprotect(string ciphertext)
    {
        var marker = ciphertext.LastIndexOf("|nonce:", StringComparison.Ordinal);
        return marker >= 0 ? ciphertext[..marker] : ciphertext;
    }

    /// <summary>Deterministic, like the real HKDF derivation.</summary>
    public byte[] DeriveKey(string purpose) =>
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("test-key:" + purpose));
}

public sealed class PassthroughRedactor : ISecretRedactor
{
    public string Redact(string text, IEnumerable<string> secretValues) => text;
}

/// <summary>Records the notifications raised so failure paths can be asserted.</summary>
public sealed class RecordingNotificationService : INotificationService
{
    public sealed record Sent(AlertEvent Event, AlertSeverity Severity, string Title, string Body);

    public List<Sent> Notifications { get; } = [];

    /// <summary>
    /// Refuses a cancelled token, like the real <c>NotificationService</c>: it queries the
    /// workspace's alert rules and posts to Discord/Telegram/a webhook with the token it was given,
    /// so a dead one delivers nothing. A fake that recorded the alert anyway would report a
    /// notification nobody received.
    /// </summary>
    public Task NotifyAsync(Guid workspaceId, AlertEvent evt, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Notifications.Add(new Sent(evt, severity, title, body));
        return Task.CompletedTask;
    }

    /// <summary>A threshold fires through its own rule, so it is recorded under that event.</summary>
    public Task<NotificationResult> NotifyRuleAsync(Guid alertId, AlertSeverity severity, string title, string body, CancellationToken ct)
    {
        Notifications.Add(new Sent(AlertEvent.ThresholdBreached, severity, title, body));
        return Task.FromResult(NotificationResult.Ok);
    }

    public Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct) =>
        Task.FromResult(NotificationResult.Ok);
}

public sealed class FixedClock(DateTimeOffset now) : ISystemClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
    public FixedClock() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }
}

/// <summary>
/// Serves canned responses to the pipeline's HTTP health probe without opening a socket, and counts
/// the attempts so "did it actually probe?" is assertable.
/// </summary>
public sealed class StubHttpClientFactory(HttpStatusCode status = HttpStatusCode.OK) : IHttpClientFactory
{
    private readonly StubHandler _handler = new(status);

    public HttpStatusCode Status { get => _handler.Status; set => _handler.Status = value; }

    /// <summary>Runs on every probe, so a test can make the container die while it is being polled.</summary>
    public Action? OnProbe { get => _handler.OnProbe; set => _handler.OnProbe = value; }

    /// <summary>
    /// Thrown instead of answering, so "nothing is listening" is reproducible without a socket. A
    /// status code cannot express it: a refused connection and a 502 are different facts, and the
    /// proxy verification only fails on the first.
    /// </summary>
    public Exception? Failure { get => _handler.Failure; set => _handler.Failure = value; }

    public int Attempts => _handler.Attempts;
    public IReadOnlyList<string> RequestedUrls => _handler.Urls;

    /// <summary>
    /// The Host header of each request. The proxy verification dials the proxy and names the domain
    /// in the header, so the header is where "which domain was checked" actually lives.
    /// </summary>
    public IReadOnlyList<string?> RequestedHosts => _handler.Hosts;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public int Attempts;
        public Action? OnProbe;
        public Exception? Failure;
        public readonly List<string> Urls = [];
        public readonly List<string?> Hosts = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            lock (Urls) Urls.Add(request.RequestUri!.ToString());
            lock (Hosts) Hosts.Add(request.Headers.Host);
            OnProbe?.Invoke();
            if (Failure is not null) return Task.FromException<HttpResponseMessage>(Failure);
            return Task.FromResult(new HttpResponseMessage(Status));
        }
    }
}
