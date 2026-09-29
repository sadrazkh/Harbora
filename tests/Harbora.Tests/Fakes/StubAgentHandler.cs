using System.Net;
using System.Text;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Stands in for an inbound <c>Harbora.Agent</c> at the HTTP layer, so the REAL
/// <c>RemoteDockerEngine</c> — its URLs, its status-code handling, its JSON — runs against a scripted
/// agent instead of a hand-written fake engine. That is what lets a test say "an agent that answers
/// 404 to the dangling endpoints" and mean the 404 the engine actually reads, not a stand-in that was
/// told what to throw.
///
/// <para>
/// It records every request it saw, so a test can assert on what was NOT asked as well as what was —
/// notably that the dangling sweep never posts to the image-remove endpoint.
/// </para>
/// </summary>
public sealed class StubAgentHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<(HttpMethod Method, string Path)> _requests = [];

    /// <summary>Every request in order, as method and path (no query).</summary>
    public IReadOnlyList<(HttpMethod Method, string Path)> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    /// <summary>The bearer token the last request carried, or null.</summary>
    public string? LastAuthorization { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (_gate) _requests.Add((request.Method, request.RequestUri!.AbsolutePath));
        LastAuthorization = request.Headers.Authorization?.ToString();
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public const string HostJson =
        """{"cpuCores":2,"totalMemoryBytes":1000,"totalDiskBytes":100000,"freeDiskBytes":50000,"dockerVersion":"29","containersRunning":0}""";

    /// <summary>
    /// An agent installed before the dangling endpoints existed: it answers everything the sweep and the
    /// report already used (tagged image list, image remove, host) and 404s for anything else — which is
    /// exactly what an ASP.NET minimal-API host does for a route it does not have.
    /// </summary>
    public static StubAgentHandler OldAgent(string taggedImagesJson = "[]") => new(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/agent/images") return Json(taggedImagesJson);
        if (request.Method == HttpMethod.Post && path == "/agent/images/remove") return new HttpResponseMessage(HttpStatusCode.OK);
        if (request.Method == HttpMethod.Get && path == "/agent/host") return Json(HostJson);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    public sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
