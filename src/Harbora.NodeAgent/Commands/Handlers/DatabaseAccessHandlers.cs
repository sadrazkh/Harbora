using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Database;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Commands.Handlers;

/// <summary>
/// Mints a temporary or persistent database credential and publishes it through an outbound tunnel.
/// The password is in the result exactly once; every later read reports the username and endpoint.
/// </summary>
public sealed class CreateDatabaseAccessGrantHandler(
    DatabaseAccessManager grants, ILogger<CreateDatabaseAccessGrantHandler> log) : INodeCommandHandler
{
    public string Command => NodeCommands.CreateDatabaseAccessGrant;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var spec = context.Envelope.PayloadAs<DatabaseAccessGrantSpec>();
        if (spec is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no grant specification.");

        if (CreateNetworkHandler.Mismatch(context, spec.TenantId) is { } refusal) return refusal;

        try
        {
            var state = await grants.CreateAsync(spec, ct);

            if (state.State != DatabaseAccessState.Active)
                return context.Fail(NodeErrorCode.TunnelUnavailable,
                    $"The grant was created but could not be published: {state.Tunnel?.LastError?.Message ?? "the gateway did not answer"}.",
                    retryable: true);

            log.LogInformation("Grant {GrantId} is live for {Engine}.", spec.GrantId, spec.Engine);
            return context.Ok(state);
        }
        catch (DatabaseAccessManager.GrantException e)
        {
            return context.Fail(e.Code, e.Message);
        }
    }
}

public sealed class RevokeDatabaseAccessGrantHandler(DatabaseAccessManager grants) : INodeCommandHandler
{
    public string Command => NodeCommands.RevokeDatabaseAccessGrant;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<RevokeDatabaseAccessRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no grant id.");

        try
        {
            return context.Ok(await grants.RevokeAsync(
                request.GrantId, context.TenantId, request.Reason, request.DropEngineUser, ct));
        }
        catch (DatabaseAccessManager.GrantException e) when (e.Code == NodeErrorCode.GrantNotFound)
        {
            // Revoking access that does not exist is the desired end state. Failing would make an
            // operator hitting "revoke" twice think something is wrong.
            return context.Ok(new AcknowledgedResult
            {
                Applied = true, NoOp = true, Detail = "no such grant on this node",
            });
        }
        catch (DatabaseAccessManager.GrantException e)
        {
            return context.Fail(e.Code, e.Message);
        }
    }
}

public sealed class RotateDatabaseAccessCredentialHandler(DatabaseAccessManager grants) : INodeCommandHandler
{
    public string Command => NodeCommands.RotateDatabaseAccessCredential;

    public async Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        var request = context.Envelope.PayloadAs<RotateDatabaseAccessRequest>();
        if (request is null) return context.Fail(NodeErrorCode.ValidationFailed, "The payload has no grant id.");

        try
        {
            return context.Ok(await grants.RotateAsync(request.GrantId, context.TenantId, request.OverlapSeconds, ct));
        }
        catch (DatabaseAccessManager.GrantException e)
        {
            return context.Fail(e.Code, e.Message);
        }
    }
}
