using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Transport;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Enrollment;

/// <summary>Result of an enrollment or renewal attempt, with the failure typed rather than thrown.</summary>
public sealed record EnrollmentOutcome<T>(T? Value, NodeError? Error) where T : class
{
    public bool Success => Error is null && Value is not null;

    public static EnrollmentOutcome<T> Ok(T value) => new(value, null);
    public static EnrollmentOutcome<T> Fail(NodeErrorCode code, string message, bool retryable = false) =>
        new(null, NodeError.From(code, message, retryable));
}

public interface IEnrollmentClient
{
    Task<EnrollmentOutcome<EnrollmentResponse>> EnrollAsync(
        string controlPlaneUrl, string enrollmentToken, EnrollmentRequest request, CancellationToken ct);

    Task<EnrollmentOutcome<CredentialRenewalResponse>> RenewAsync(
        string controlPlaneUrl, NodeIdentity identity, CredentialRenewalRequest request, CancellationToken ct);
}

/// <summary>
/// The HTTPS half of the contract. Both calls are outbound; nothing here ever listens.
///
/// <para>
/// Failures are returned, not thrown. Enrollment runs in a retry loop at boot and renewal runs on a
/// timer, and both need to distinguish "try again in a minute" from "an admin has to do something"
/// — a thrown exception flattens that distinction into a stack trace.
/// </para>
/// </summary>
public sealed class HttpEnrollmentClient(ControlPlaneTls tls, ILogger<HttpEnrollmentClient> log) : IEnrollmentClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<EnrollmentOutcome<EnrollmentResponse>> EnrollAsync(
        string controlPlaneUrl, string enrollmentToken, EnrollmentRequest request, CancellationToken ct)
    {
        // No client certificate: there is not one yet. This single exchange is what produces it.
        using var handler = tls.BuildHandler(identity: null);
        using var client = CreateClient(handler, controlPlaneUrl);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrollmentToken);

        return await SendAsync<EnrollmentRequest, EnrollmentResponse>(
            client, NodeContract.EnrollmentPath, request, ct);
    }

    public async Task<EnrollmentOutcome<CredentialRenewalResponse>> RenewAsync(
        string controlPlaneUrl, NodeIdentity identity, CredentialRenewalRequest request, CancellationToken ct)
    {
        using var handler = tls.BuildHandler(identity);
        using var client = CreateClient(handler, controlPlaneUrl);

        return await SendAsync<CredentialRenewalRequest, CredentialRenewalResponse>(
            client, NodeContract.CredentialRenewPath, request, ct);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler, string baseUrl) =>
        new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout,
        };

    private async Task<EnrollmentOutcome<TResponse>> SendAsync<TRequest, TResponse>(
        HttpClient client, string path, TRequest body, CancellationToken ct)
        where TResponse : class
    {
        try
        {
            using var response = await client.PostAsJsonAsync(path, body, NodeContract.Json, ct);

            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<TResponse>(NodeContract.Json, ct);
                return value is null
                    ? EnrollmentOutcome<TResponse>.Fail(NodeErrorCode.MalformedEnvelope, "Control plane returned an empty body.", retryable: true)
                    : EnrollmentOutcome<TResponse>.Ok(value);
            }

            var failure = await ReadFailureAsync(response, ct);
            return EnrollmentOutcome<TResponse>.Fail(
                failure.Code, failure.Message, Retryable(response.StatusCode));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return EnrollmentOutcome<TResponse>.Fail(NodeErrorCode.Timeout, $"{path} timed out.", retryable: true);
        }
        catch (HttpRequestException e)
        {
            // Includes TLS failures. Retryable: a panel behind a restarting proxy looks exactly
            // like this, and giving up would leave the node permanently unenrolled.
            log.LogDebug(e, "Transport failure calling {Path}.", path);
            return EnrollmentOutcome<TResponse>.Fail(NodeErrorCode.Internal, $"Could not reach the control plane: {e.Message}", retryable: true);
        }
    }

    private static async Task<EnrollmentFailure> ReadFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var failure = await response.Content.ReadFromJsonAsync<EnrollmentFailure>(NodeContract.Json, ct);
            if (failure is not null && !string.IsNullOrWhiteSpace(failure.Message)) return failure;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException)
        {
            // A proxy's HTML error page is not a contract violation worth failing differently for.
        }

        return new EnrollmentFailure
        {
            Code = FromStatus(response.StatusCode),
            Message = $"Control plane answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
    }

    private static NodeErrorCode FromStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => NodeErrorCode.EnrollmentTokenInvalid,
        HttpStatusCode.Forbidden => NodeErrorCode.CredentialRevoked,
        HttpStatusCode.Conflict => NodeErrorCode.EnrollmentTokenAlreadyUsed,
        HttpStatusCode.Gone => NodeErrorCode.EnrollmentTokenExpired,
        HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest => NodeErrorCode.ValidationFailed,
        HttpStatusCode.NotFound => NodeErrorCode.CredentialRevoked,
        _ => NodeErrorCode.Internal,
    };

    /// <summary>
    /// A 5xx or a 429 is the control plane's problem and will pass. A 4xx about the token is the
    /// operator's and never will — retrying it just burns the log until someone reads it.
    /// </summary>
    private static bool Retryable(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.TooManyRequests || status == HttpStatusCode.RequestTimeout;
}
