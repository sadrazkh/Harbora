namespace Harbora.Web.ViewModels;

/// <summary>Backs <c>/apps/{id}/config-overrides</c> (C2, 2026-08-22 config-delivery plan).</summary>
public sealed class ConfigOverridesPageViewModel
{
    public Guid AppId { get; set; }
    public string AppName { get; set; } = string.Empty;
    public List<ConfigOverrideRuleRow> Rules { get; set; } = [];

    /// <summary>Set only right after a "validate this rule" POST, for the one rule it targeted.</summary>
    public ConfigOverrideValidationRow? Validation { get; set; }
}

/// <summary><paramref name="Value"/> is null for a secret or an attached-service reference — never
/// sent to the page, the same masking every other secret gets.</summary>
public sealed record ConfigOverrideRuleRow(
    Guid Id, string FilePath, string? FormatOverride, string KeyPath, string ValueKindLabel,
    string? Value, bool IsMasked, bool HasUnpublishedChanges);

/// <summary>The result of "validate this rule against the deployed app" — read the file, resolve the
/// key path, show the current value and what it would become, before ever redeploying.</summary>
public sealed record ConfigOverrideValidationRow(
    Guid RuleId, bool Ok, string? CurrentValue, string? WouldBecomeValue, bool WouldBecomeIsSecret, string? FailureDetail);
