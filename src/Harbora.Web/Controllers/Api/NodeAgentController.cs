using Harbora.Infrastructure.Nodes;
using Harbora.NodeAgent.Contracts;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harbora.Web.Controllers.Api;

/// <summary>
/// The node-facing half of <c>contracts/node-agent/v1</c>: enrollment and credential renewal.
///
/// <para>
/// Both endpoints are anonymous to the panel's own auth stack, because a node has neither a session
/// nor an API token — it has a single-use enrollment token or a client certificate, and this
/// controller is where those are checked. Nothing here reads the workspace scope, so every database
/// read underneath uses <c>IgnoreQueryFilters</c>: an anonymous request is scoped to an empty
/// workspace, and a filtered read would come back empty while reporting success.
/// </para>
/// </summary>
[ApiController]
[Route("api/node-agent/v1")]
[AllowAnonymous]
public sealed class NodeAgentController(
    NodeEnrollmentService enrollment,
    NodeClientCertificateResolver certificates,
    ILogger<NodeAgentController> log) : ControllerBase
{
    /// <summary>
    /// Exchange a single-use enrollment token for a permanent node identity.
    ///
    /// <para>
    /// Rate-limited on the same per-IP bucket as the login form. Enrollment tokens are long random
    /// strings, so guessing one is not a realistic attack, but an endpoint that signs certificates
    /// should not be the one place on the panel with no ceiling on attempts.
    /// </para>
    /// </summary>
    [HttpPost("enroll")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> Enroll([FromBody] EnrollmentRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(Failure(NodeErrorCode.ValidationFailed, "The request body could not be read."));

        var token = BearerToken() ?? request.EnrollmentToken;

        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(Failure(NodeErrorCode.EnrollmentTokenInvalid, "No enrollment token was supplied."));

        if (string.IsNullOrWhiteSpace(request.CertificateSigningRequestPem))
            return UnprocessableEntity(Failure(NodeErrorCode.ValidationFailed, "A certificate signing request is required."));

        var outcome = await enrollment.EnrollAsync(token, request, SourceIp(), ct);

        if (outcome.Success) return Ok(outcome.Value);

        // The status code carries the same meaning as the code in the body, so a client that only
        // looks at one of them still behaves correctly.
        return StatusCode(StatusFor(outcome.Error!.Value), Failure(outcome.Error.Value, outcome.Message!));
    }

    /// <summary>
    /// Rotate a node's certificate, authenticated by the one it is replacing.
    ///
    /// <para>
    /// Not rate-limited: a node that cannot renew eventually cannot connect, and throttling the
    /// recovery path of a fleet that all enrolled on the same day is how a staggered renewal becomes
    /// a synchronised outage.
    /// </para>
    /// </summary>
    [HttpPost("credential/renew")]
    public async Task<IActionResult> Renew([FromBody] CredentialRenewalRequest? request, CancellationToken ct)
    {
        if (request is null)
            return BadRequest(Failure(NodeErrorCode.ValidationFailed, "The request body could not be read."));

        var certificate = certificates.Resolve(HttpContext);

        if (certificate is null)
        {
            log.LogWarning(
                "A credential renewal for node {NodeId} arrived with no client certificate. " +
                "Either Kestrel is not configured to ask for one, or Traefik is not forwarding it and " +
                "NodeAgent:TrustForwardedClientCertificate is off.",
                request.NodeId);

            return Unauthorized(Failure(NodeErrorCode.Unauthorized,
                "This endpoint requires the node's client certificate."));
        }

        using (certificate)
        {
            var outcome = await enrollment.RenewAsync(certificate, request, SourceIp(), ct);

            if (outcome.Success) return Ok(outcome.Value);

            return StatusCode(StatusFor(outcome.Error!.Value), Failure(outcome.Error.Value, outcome.Message!));
        }
    }

    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();

        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }

    private string? SourceIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private static EnrollmentFailure Failure(NodeErrorCode code, string message) =>
        new() { Code = code, Message = message };

    /// <summary>The contract fixes these; see contracts/node-agent/v1/openapi.yaml.</summary>
    private static int StatusFor(NodeErrorCode code) => code switch
    {
        NodeErrorCode.EnrollmentTokenInvalid => StatusCodes.Status401Unauthorized,
        NodeErrorCode.Unauthorized => StatusCodes.Status401Unauthorized,
        NodeErrorCode.EnrollmentTokenAlreadyUsed => StatusCodes.Status409Conflict,
        NodeErrorCode.EnrollmentTokenExpired => StatusCodes.Status410Gone,
        NodeErrorCode.CredentialRevoked => StatusCodes.Status403Forbidden,
        NodeErrorCode.ValidationFailed => StatusCodes.Status422UnprocessableEntity,
        NodeErrorCode.UnsupportedArchitecture => StatusCodes.Status422UnprocessableEntity,
        NodeErrorCode.UnsupportedProtocolVersion => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status500InternalServerError,
    };
}
