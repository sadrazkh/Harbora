using Harbora.Domain.Apps;
using Harbora.Domain.Configuration;

namespace Harbora.Application.Abstractions;

/// <summary>
/// Resolves and applies an app's <see cref="ConfigOverrideRule"/>s (C2, 2026-08-22 config-delivery
/// plan) — the seam <c>DeploymentPipeline</c> calls at deploy time, and the same one the panel's
/// pre-deploy validation reads through.
/// </summary>
public interface IConfigOverrideResolver
{
    /// <summary>
    /// Resolves every rule for <paramref name="app"/> and applies each to the named file inside the
    /// given container, via <see cref="IContainerConfigFileWriter"/>. Throws
    /// <see cref="ConfigOverrideException"/> — carrying every failing rule's actionable diagnostic,
    /// never a secret value — when any rule cannot be applied, by design: the caller's ordinary
    /// deployment-failure handling then fails the deployment with no new plumbing, satisfying "a
    /// failed override fails the deployment" without a second failure path to keep in sync with the
    /// first.
    /// </summary>
    Task ApplyAllAsync(App app, string containerNameOrId, CancellationToken ct);

    /// <summary>
    /// The read-only half: resolve one rule's value and show what applying it would do, without
    /// writing anything. Powers "validate a rule against the deployed app before deploying" — read
    /// the file, resolve the key path, show the current value and what it would become.
    /// </summary>
    Task<ConfigOverridePreview> PreviewAsync(App app, ConfigOverrideRule rule, string containerNameOrId, CancellationToken ct);
}

/// <summary>One rule's diagnostic, safe to put in a deploy log or an error banner by construction —
/// never carries a value, only the facts named in the plan's own list of what "actionable" means.</summary>
public sealed record ConfigOverrideFailure(
    Guid RuleId, string FilePath, string KeyPath, ConfigOverrideFailureReason Reason, string Detail)
{
    public override string ToString() => $"{FilePath}:{KeyPath} — {Detail}";
}

/// <summary>Every rule that failed, thrown as one exception so a single deployment failure names
/// every broken rule at once rather than the first of a redeploy-and-pray loop.</summary>
public sealed class ConfigOverrideException(IReadOnlyList<ConfigOverrideFailure> failures)
    : Exception(BuildMessage(failures))
{
    public IReadOnlyList<ConfigOverrideFailure> Failures { get; } = failures;

    private static string BuildMessage(IReadOnlyList<ConfigOverrideFailure> failures) =>
        failures.Count == 1
            ? $"Config override could not be applied: {failures[0]}"
            : $"{failures.Count} config overrides could not be applied: " + string.Join(" | ", failures);
}

/// <summary>
/// What validating one rule against a deployed container found. <see cref="Failure"/> is set only
/// when <see cref="Ok"/> is false. <see cref="WouldBecomeValue"/> is null (never a placeholder secret
/// string) when the rule's value kind is secret-shaped — masked the same way the panel masks every
/// other secret.
/// </summary>
public sealed record ConfigOverridePreview(
    bool Ok, string? CurrentValue, string? WouldBecomeValue, bool WouldBecomeIsSecret, ConfigOverrideFailure? Failure);
