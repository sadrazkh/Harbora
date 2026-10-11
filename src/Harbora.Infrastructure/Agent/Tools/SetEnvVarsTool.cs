using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>set_env_vars</c> — creates or overwrites environment variables on an app through
/// <see cref="IAppConfigurationService.SetEnvAsync"/>, the path the app page's form takes.
///
/// <para>
/// A secret's value goes from the call's arguments straight into the service, which encrypts it, and
/// nowhere else this tool writes: not the card, not the result the model reads, not the summary the
/// person reads — keys and flags only. (The arguments themselves are the model's own words; the plan
/// keeps them to run the step.) A plain value is shown on the card, shortened, because the person
/// deciding whether to approve "PORT = 8080" should be able to read it.
/// </para>
/// </summary>
public sealed class SetEnvVarsTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppConfigurationService configuration) : AgentAppTool(resolver, access)
{
    private const int ShownValueLength = 60;

    public override string Name => "set_env_vars";

    public override string Description =>
        "Sets one or more environment variables on an app, creating them or overwriting the ones that exist (value and " +
        "flags). Mark API keys, passwords and tokens as secret: their values are stored encrypted and are never shown " +
        "again, not even to you. Changes take effect on the app's next deploy. Nothing changes until the user approves.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug (or one an earlier step of this plan creates)." },
            "variables": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "key": { "type": "string", "description": "The variable's name, e.g. DATABASE_URL." },
                  "value": { "type": "string", "description": "The value. May be empty." },
                  "secret": { "type": "boolean", "description": "Store encrypted and never show the value again. Default false." },
                  "availableAtBuild": { "type": "boolean", "description": "Also pass it to the build, not only the running app. Default false." }
                },
                "required": ["key", "value"],
                "additionalProperties": false
              }
            }
          },
          "required": ["app", "variables"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.AppsEnv;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var (variables, error) = Read(args);
        if (error is { } refused) return AgentValidation.Refused(refused.En, refused.Fa);

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);

        // What each key holds now, so the card can say which values it replaces — flags only, never a value.
        var current = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (target.App is { } app)
        {
            var refusals = await configuration.ValidateSetEnvAsync(ActorOf(context), app.Id, variables, ct);
            if (refusals.Count > 0) return Refused(refusals);

            var keys = variables.Select(v => v.Key).ToList();
            current = await db.EnvironmentVariables.AsNoTracking()
                .Where(e => e.AppId == app.Id && keys.Contains(e.Key))
                .ToDictionaryAsync(e => e.Key, e => e.IsSecret, StringComparer.Ordinal, ct);
        }

        var slug = target.App?.Slug ?? target.Planned!.Name;
        var detailEn = new List<string>();
        var detailFa = new List<string>();
        var warningsEn = new List<string> { "Takes effect on the app's next deploy." };
        var warningsFa = new List<string> { "با استقرار بعدی اپ اعمال می‌شود." };

        foreach (var variable in variables)
        {
            var en = variable.IsSecret ? $"{variable.Key} (secret — value hidden)" : $"{variable.Key} = {Shown(variable.Value, "(empty)")}";
            var fa = variable.IsSecret ? $"{variable.Key} (محرمانه — مقدار نمایش داده نمی‌شود)" : $"{variable.Key} = {Shown(variable.Value, "(خالی)")}";
            if (variable.AvailableAtBuild) { en += ", also at build"; fa += "، هنگام build هم"; }
            if (current.TryGetValue(variable.Key, out var wasSecret))
            {
                en += ", replaces the current value";
                fa += "، مقدار فعلی را جایگزین می‌کند";
                if (wasSecret && !variable.IsSecret)
                {
                    warningsEn.Add($"{variable.Key} is secret now; this stores it as a plain value that anyone who can see the app can read.");
                    warningsFa.Add($"{variable.Key} الان محرمانه است؛ این کار آن را به‌صورت مقدار معمولی ذخیره می‌کند که هر کس اپ را ببیند می‌تواند بخواند.");
                }
            }
            detailEn.Add(en);
            detailFa.Add(fa);
        }

        if (target.Planned is { } planned)
        {
            var (en, fa) = PlannedNote(planned);
            detailEn.Add(en);
            detailFa.Add(fa);
        }

        return new AgentValidation(true,
            TitleEn: variables.Count == 1
                ? $"Set environment variable {variables[0].Key} on {slug}"
                : $"Set {variables.Count} environment variables on {slug}",
            TitleFa: variables.Count == 1
                ? $"تنظیم متغیر محیطی {variables[0].Key} روی {slug}"
                : $"تنظیم {variables.Count} متغیر محیطی روی {slug}",
            DetailEn: string.Join("; ", detailEn),
            DetailFa: string.Join("؛ ", detailFa),
            WarningsEn: warningsEn,
            WarningsFa: warningsFa);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (variables, error) = Read(args);
        if (error is { } refused) return NotPossible(refused.En, refused.Fa);

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var outcome = await configuration.SetEnvAsync(ActorOf(context), app.Id, variables, ct);
        if (!outcome.Ok) return Failed(outcome.Refusals);
        var changes = outcome.Value!;

        static string LineEn(AppEnvVarChange c) => $"{c.Key} ({(c.IsSecret ? "secret, " : "")}{(c.Created ? "new" : "replaced")})";
        static string LineFa(AppEnvVarChange c) => $"{c.Key} ({(c.IsSecret ? "محرمانه، " : "")}{(c.Created ? "جدید" : "جایگزین شد")})";

        return new AgentToolResult(true,
            AgentJson.Serialize(new
            {
                app = app.Slug,
                variables = changes.Select(c => new { key = c.Key, created = c.Created, secret = c.IsSecret, availableAtBuild = c.AvailableAtBuild }),
                appliesOn = "next deploy"
            }),
            $"Set {string.Join(", ", changes.Select(LineEn))} on '{app.Slug}'. It takes effect on the app's next deploy.",
            $"{string.Join("، ", changes.Select(LineFa))} روی «{app.Slug}» تنظیم شد. با استقرار بعدی اپ اعمال می‌شود.",
            Links: [AppLink(app)]);
    }

    /// <summary>
    /// The variables as the service takes them, the same key given twice collapsed to its last value (as
    /// the service would apply them). Values are taken as given — not trimmed: a secret's spaces are its
    /// own business — and a key that could never be set in a container (blank, with "=" or whitespace in
    /// it) is refused here rather than stored and found broken at deploy time.
    /// </summary>
    private static (IReadOnlyList<AppEnvVarInput> Variables, (string En, string Fa)? Error) Read(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("variables", out var list)
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return ([], ("Give at least one variable to set, as variables: [{ key, value }].",
                "دست‌کم یک متغیر برای تنظیم بدهید، به شکل variables: [{ key, value }]."));

        var variables = new List<AppEnvVarInput>();
        foreach (var item in list.EnumerateArray())
        {
            var key = AgentArgs.String(item, "key");
            if (key is null) return ([], ("Every variable needs a key.", "هر متغیر به نام (key) نیاز دارد."));
            if (key.Any(c => c == '=' || char.IsWhiteSpace(c) || char.IsControl(c)))
                return ([], ($"'{key}' cannot be a variable name: names contain no '=' and no spaces.",
                    $"«{key}» نمی‌تواند نام متغیر باشد: نام متغیر «=» یا فاصله ندارد."));

            if (!item.TryGetProperty("value", out var raw) || raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || raw.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return ([], ($"Give a value for {key} (it may be empty).", $"برای {key} مقدار بدهید (می‌تواند خالی باشد)."));
            var value = raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : raw.GetRawText();

            variables.Add(new AppEnvVarInput(key, value,
                AgentArgs.Bool(item, "secret") ?? false,
                AgentArgs.Bool(item, "availableAtBuild") ?? false));
        }

        return (variables.GroupBy(v => v.Key, StringComparer.Ordinal).Select(g => g.Last()).ToList(), null);
    }

    private static string Shown(string? value, string empty) => value switch
    {
        null or "" => empty,
        { Length: > ShownValueLength } lengthy => lengthy[..ShownValueLength] + "…",
        var text => text
    };
}
