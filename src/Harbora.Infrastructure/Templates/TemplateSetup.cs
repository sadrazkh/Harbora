using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Templates;

/// <summary>A variable an app should be created with.</summary>
/// <param name="Value">Null when the manifest gives no default and nothing can be invented for it.</param>
/// <param name="NeedsAValue">True when someone must supply it before the app will work.</param>
public sealed record PreparedVariable(string Key, string? Value, bool Secret, bool NeedsAValue, string? Description);

/// <summary>What creating an app from a template should produce, besides the app itself.</summary>
/// <param name="Kind">
/// What the created app should be. Read from the manifest's own "kind"; defaults to
/// <see cref="ServiceKind.Web"/>, which is what every template written before "kind" existed
/// already got by never setting <c>App.Kind</c> at all.
/// </param>
public sealed record TemplateSetupPlan(
    IReadOnlyList<PreparedVariable> Variables,
    IReadOnlyList<string> VolumeMounts,
    IReadOnlyList<string> RequiresServices,
    ServiceKind Kind);

/// <summary>
/// Turns a template manifest into the configuration an app is created with.
///
/// Until this existed the manifest was read for its image and port and nothing else, so a template
/// that declared a volume produced an app without one — a static site whose content vanished on
/// every redeploy — and a template that declared <c>APP_KEY (secret)</c> produced an app missing it.
/// </summary>
public static class TemplateSetup
{
    /// <summary>
    /// A secret with no default is generated rather than left blank. A framework that will not boot
    /// without an application key should not need the person deploying it to know that.
    /// </summary>
    public static TemplateSetupPlan Prepare(TemplateManifest manifest, Func<string> generateSecret)
    {
        var variables = manifest.Variables.Select(variable =>
        {
            if (!string.IsNullOrEmpty(variable.Default))
                return new PreparedVariable(variable.Key, variable.Default, variable.Secret, false, variable.Description);

            // Required and secret: a third-party credential — a Kavenegar API key, a Telegram bot
            // token — that only the person deploying has. Asked for like a plain required variable,
            // but still masked and encrypted like any other secret; the two were previously the same
            // flag, which made asking for one impossible without also inventing the other.
            if (variable.Required && variable.Secret)
                return new PreparedVariable(variable.Key, null, true, true, variable.Description);

            return variable.Secret
                ? new PreparedVariable(variable.Key, generateSecret(), true, false, variable.Description)
                // A plain variable with no default is something only the person deploying knows —
                // a hostname, an address. It is created empty and flagged, rather than invented.
                : new PreparedVariable(variable.Key, null, false, true, variable.Description);
        }).ToList();

        return new TemplateSetupPlan(
            variables,
            manifest.Volumes.Select(v => v.MountPath).Distinct(StringComparer.Ordinal).ToList(),
            manifest.Requires,
            ResolveKind(manifest.Kind));
    }

    /// <summary>
    /// The manifest's "kind" string turned into the real enum, defaulting to <see cref="ServiceKind.Web"/>
    /// for anything unrecognised — <see cref="TemplateManifest.TryParse"/> already refuses a "kind" that
    /// is not one of the three known words, so by the time this runs the only unrecognised value
    /// possible is null, which means "web" on purpose.
    /// </summary>
    private static ServiceKind ResolveKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "worker" => ServiceKind.Worker,
        "private" => ServiceKind.Private,
        _ => ServiceKind.Web
    };

    /// <summary>
    /// What to tell someone after the app is created. Silence about a variable they still have to
    /// fill in is how an app sits broken with no indication why.
    /// </summary>
    public static string? Advice(TemplateSetupPlan plan)
    {
        var missing = plan.Variables.Where(v => v.NeedsAValue).Select(v => v.Key).ToList();
        var parts = new List<string>();

        if (missing.Count > 0)
            parts.Add($"Set {string.Join(", ", missing)} before deploying — the template leaves those to you.");

        if (plan.RequiresServices.Count > 0)
            parts.Add($"This template also needs {string.Join(", ", plan.RequiresServices)}; " +
                      "create it from the Databases page and attach it.");

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }
}
