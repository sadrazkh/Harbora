using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Harbora.Infrastructure.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers;

/// <summary>
/// Harbora's AI endpoint, shaped like OpenAI's so existing clients work unchanged.
///
/// The customer authenticates with a Harbora key and never sees a provider token. Requests are made
/// server-side, so a provider sees Harbora's infrastructure rather than the customer's address, and
/// revoking a key here really revokes access — unlike a provider token handed out, which keeps
/// working wherever it was pasted.
///
/// Anonymous to the cookie pipeline on purpose: this is an API authenticated by bearer key, and
/// running it through the session filters would look for a workspace that a key-authenticated
/// request does not have yet.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("v1")]
public sealed class AiGatewayController(
    AiGatewayService gateway,
    IEnumerable<IAiProviderAdapter> adapters,
    ILogger<AiGatewayController> logger) : ControllerBase
{
    /// <summary>Bodies past this are refused before they are parsed.</summary>
    private const int MaxRequestBytes = 2 * 1024 * 1024;

    /// <summary>Retries across credentials. Beyond this the caller is waiting on a lost cause.</summary>
    private const int MaxAttempts = 3;

    [HttpGet("models")]
    public async Task<IActionResult> Models(CancellationToken ct)
    {
        var caller = await AuthenticateAsync(ct);
        if (caller is null) return Unauthorized(Error("invalid_api_key", "Invalid API key."));

        var models = await gateway.ModelsForAsync(caller, ct);

        // The OpenAI shape, so a client library can list models without special-casing Harbora.
        return Ok(new
        {
            @object = "list",
            data = models.Select(m => new
            {
                id = m.Alias,
                @object = "model",
                owned_by = "harbora",
                context_length = m.ContextLength,
                max_output_tokens = AiPlanAccess.EffectiveMaxOutput(caller.Plan, m)
            })
        });
    }

    [HttpPost("chat/completions")]
    public Task<IActionResult> ChatCompletions(CancellationToken ct) => ForwardAsync("chat/completions", ct);

    [HttpPost("responses")]
    public Task<IActionResult> Responses(CancellationToken ct) => ForwardAsync("responses", ct);

    [HttpPost("embeddings")]
    public Task<IActionResult> Embeddings(CancellationToken ct) => ForwardAsync("embeddings", ct);

    /// <summary>
    /// The one path every forwarded request takes.
    ///
    /// Written once rather than per endpoint: three near-identical copies is how one of them ends up
    /// missing the quota check.
    /// </summary>
    private async Task<IActionResult> ForwardAsync(string endpoint, CancellationToken ct)
    {
        var correlationId = HttpContext.TraceIdentifier;

        var caller = await AuthenticateAsync(ct);
        if (caller is null) return Unauthorized(Error("invalid_api_key", "Invalid API key."));

        var body = await ReadBodyAsync(ct);
        if (body is null)
            return StatusCode(413, Error("request_too_large", "The request body is too large."));

        if (!TryRead(body, out var requestedModel, out var maxTokens, out var streaming))
            return BadRequest(Error("invalid_request", "The request body is not valid JSON."));

        // Embeddings have no streamed form; asking for one is a request that cannot be served.
        if (streaming && endpoint == "embeddings")
            return BadRequest(Error("streaming_unsupported", "Embeddings cannot be streamed."));

        var tried = new HashSet<Guid>();
        AiRefusal? lastRefusal = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var (routed, refusal) = await gateway.RouteAsync(
                caller, requestedModel, maxTokens, streaming, tried, ct);

            if (routed is null)
            {
                lastRefusal = refusal;

                // A refusal about the request itself will not change by trying again.
                if (refusal is not null && refusal.StatusCode is 400 or 402 or 403) break;
                continue;
            }

            tried.Add(routed.Credential.Id);

            var wait = AiFailureClassifier.Backoff(attempt);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);

            if (streaming)
                return await StreamAsync(routed, body, endpoint, correlationId, ct);

            var outcome = await SendAsync(routed, body, endpoint, correlationId, ct);
            if (outcome.Handled) return outcome.Result!;

            lastRefusal = new AiRefusal(502, "upstream_failed", "The AI provider could not be reached.");
        }

        var final = lastRefusal ?? new AiRefusal(503, "no_capacity", "No capacity is available.");
        return StatusCode(final.StatusCode, Error(final.Code, final.Message));
    }

    /// <summary>Sends a plain request, meters it, and says whether the answer is final.</summary>
    private async Task<(bool Handled, IActionResult? Result)> SendAsync(
        AiRoutedRequest routed, string body, string endpoint, string correlationId, CancellationToken ct)
    {
        var adapter = AdapterFor(routed);
        if (adapter is null) return (true, StatusCode(503, Error("no_adapter", "No adapter for that provider.")));

        var stopwatch = Stopwatch.StartNew();
        using var _ = AiGatewayService.Occupy(routed.Credential.Id);

        var result = await adapter.SendAsync(
            routed.Provider, routed.Token, routed.Model, body, endpoint, ct);

        stopwatch.Stop();

        if (result.Ok)
        {
            AiCredentialRouter.NoteSuccess(routed.Credential, DateTimeOffset.UtcNow);
            await gateway.MeterAsync(routed, result.InputTokens, result.OutputTokens, result.CachedInputTokens,
                200, (int)stopwatch.ElapsedMilliseconds, false, false, correlationId, null, ct);

            return (true, Content(result.Body ?? "{}", "application/json"));
        }

        var verdict = AiFailureClassifier.Classify(result.StatusCode, result.RetryAfterHeader, result.Exception);
        AiCredentialRouter.NoteFailure(
            routed.Credential, DateTimeOffset.UtcNow, verdict.Kind.ToString(),
            verdict.ParkCredential ? verdict.RetryAfter : null);

        // Metered even in failure: the attempt is part of the record, and a failure that leaves no
        // trace is one nobody can investigate.
        await gateway.MeterAsync(routed, 0, 0, 0, result.StatusCode ?? 502,
            (int)stopwatch.ElapsedMilliseconds, false, false, correlationId, verdict.Kind.ToString(), ct);

        // A bad request is the customer's to fix; passing the provider's own message back is more
        // useful than a generic error.
        if (!verdict.RetryElsewhere)
            return (true, StatusCode(result.StatusCode ?? 400, Content(result.Body ?? "{}", "application/json")));

        return (false, null);
    }

    /// <summary>
    /// Streams a response back as server-sent events.
    ///
    /// Usage is settled in a finally block, because the common ending is the customer closing the
    /// connection early — and the provider has already charged us for the tokens produced by then.
    /// Not recording those is a real cost with no record against it.
    /// </summary>
    private async Task<IActionResult> StreamAsync(
        AiRoutedRequest routed, string body, string endpoint, string correlationId, CancellationToken ct)
    {
        var adapter = AdapterFor(routed);
        if (adapter is null) return StatusCode(503, Error("no_adapter", "No adapter for that provider."));

        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var stopwatch = Stopwatch.StartNew();
        long input = 0, output = 0, cached = 0;
        var disconnected = false;
        var started = false;

        using var occupancy = AiGatewayService.Occupy(routed.Credential.Id);

        try
        {
            await foreach (var chunk in adapter.StreamAsync(
                routed.Provider, routed.Token, routed.Model, body, endpoint, ct))
            {
                started = true;

                // Providers report usage on the final frames; read every chunk rather than only the
                // last, since some send it just before [DONE].
                if (chunk.Data != "[DONE]")
                {
                    var (i, o, c) = AiUsageParser.Read(chunk.Data);
                    if (i > 0) input = i;
                    if (o > 0) output = o;
                    if (c > 0) cached = c;
                }

                await Response.WriteAsync($"data: {chunk.Data}\n\n", ct);
                await Response.Body.FlushAsync(ct);

                if (chunk.IsFinal) break;
            }

            AiCredentialRouter.NoteSuccess(routed.Credential, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            // The customer went away. Everything produced up to here was still billed to us.
            disconnected = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Streaming from {Provider} failed.", routed.Provider.Name);
            AiCredentialRouter.NoteFailure(routed.Credential, DateTimeOffset.UtcNow, "stream failed");

            // Only safe to report an error if nothing has been written yet — otherwise the client
            // is mid-answer and an error frame would corrupt what they have.
            if (!started) return StatusCode(502, Error("upstream_failed", "The stream could not be started."));
        }
        finally
        {
            stopwatch.Stop();

            // CancellationToken.None: the request token is already cancelled when the customer
            // disconnects, and metering must still be written.
            await gateway.MeterAsync(routed, input, output, cached,
                disconnected ? 499 : 200, (int)stopwatch.ElapsedMilliseconds,
                streaming: true, clientDisconnected: disconnected,
                correlationId, disconnected ? "client disconnected" : null, CancellationToken.None);
        }

        return new EmptyResult();
    }

    private IAiProviderAdapter? AdapterFor(AiRoutedRequest routed) =>
        adapters.FirstOrDefault(a => a.Handles == routed.Provider.Type)
        ?? adapters.FirstOrDefault();

    private Task<AiCaller?> AuthenticateAsync(CancellationToken ct)
    {
        var presented = AiApiKeys.FromAuthorizationHeader(Request.Headers.Authorization.ToString());
        return gateway.AuthenticateAsync(presented, ct);
    }

    /// <summary>Reads the body, refusing anything oversized before it is parsed.</summary>
    private async Task<string?> ReadBodyAsync(CancellationToken ct)
    {
        if (Request.ContentLength is > MaxRequestBytes) return null;

        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);

        return body.Length > MaxRequestBytes ? null : body;
    }

    /// <summary>Pulls out only what routing needs. The rest is forwarded untouched.</summary>
    private static bool TryRead(string body, out string model, out int? maxTokens, out bool streaming)
    {
        model = "";
        maxTokens = null;
        streaming = false;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                model = m.GetString() ?? "";

            if (root.TryGetProperty("max_tokens", out var mt) && mt.TryGetInt32(out var value))
                maxTokens = value;
            else if (root.TryGetProperty("max_output_tokens", out var mo) && mo.TryGetInt32(out var alt))
                maxTokens = alt;

            if (root.TryGetProperty("stream", out var s) && s.ValueKind is JsonValueKind.True)
                streaming = true;

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The OpenAI error shape, so client libraries surface it properly.</summary>
    private static object Error(string code, string message) =>
        new { error = new { message, type = "invalid_request_error", code } };
}
