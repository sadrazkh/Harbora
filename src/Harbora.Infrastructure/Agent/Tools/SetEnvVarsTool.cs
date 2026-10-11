using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>set_env_vars</c> — creates or overwrites environment variables on an app through
/// <see cref="IAppConfigurationService.SetEnvAsync"/>, the path the app page's form takes.
///
/// <para>
/// <b>A secret's value never travels through the model.</b> It arrives one of two ways: as a handle
/// (<c>{{secret:…}}</c>) the person made with the pane's lock button, which only the vault can turn back
/// into the value — checked to exist when the step is carded, resolved only when it runs — or as
/// <c>generate: true</c>, a strong random value made on the server at execution and never written into
/// the arguments. A plain value for a secret is refused with that guidance, so the plan, the conversation
/// and the provider never hold it. Cards and results show <see cref="AgentSecretHandles.Masked"/>.
/// A plain variable's value is shown on the card, shortened: the person approving "PORT = 8080" should be
/// able to read it.
/// </para>
///
/// <para>
/// <b>Flags left out are kept, not reset.</b> The service overwrites both flags on every set, so a call
/// that omits <c>secret</c> used to store a secret's new value in plain text. Omitted flags now take the
/// existing row's values (new variables: plain, runtime only), and an explicit <c>secret: false</c> on a
/// secret is refused — turning a secret into plain text is not something to do through the assistant.
/// </para>
/// </summary>
public sealed class SetEnvVarsTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppConfigurationService configuration,
    IAgentSecretVault vault) : AgentAppTool(resolver, access)
{
    private const int ShownValueLength = 60;

    /// <summary>The generated secret's length, from the alphabet the platform's database passwords use.</summary>
    public const int GeneratedLength = 48;

    public override string Name => "set_env_vars";

    public override string Description =>
        "Sets one or more environment variables on an app, creating them or overwriting the ones that exist. A secret's " +
        "value must never travel through the chat: never ask the user to type a password, token or API key. Ask them to " +
        "enter it with the lock button beside the message box; it reaches you as " + HandleShape + ", which you pass " +
        "unchanged as the value — it is stored encrypted and you never see it. For a secret nobody needs to type " +
        "(JWT_SECRET, SESSION_KEY…) pass generate: true instead of a value. A plain value for a secret is refused. " +
        "Leaving secret or availableAtBuild out keeps the variable's current setting (new variables: plain, runtime only); " +
        "a secret is never turned into plain text here. Changes take effect on the app's next deploy. Nothing changes " +
        "until the user approves.";

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
                  "value": { "type": "string", "description": "The value (may be empty), or the {{secret:…}} handle the user entered with the lock button. Leave out when generate is true." },
                  "generate": { "type": "boolean", "description": "Generate a strong random secret on the server instead of giving a value. Default false." },
                  "secret": { "type": "boolean", "description": "Store encrypted. Leave out to keep the current setting (new variables: false). A handle or generate is always secret." },
                  "availableAtBuild": { "type": "boolean", "description": "Also pass it to the build. Leave out to keep the current setting (new variables: false)." }
                },
                "required": ["key"],
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
        var (requested, error) = Read(args);
        if (error is { } refused) return AgentValidation.Refused(refused.En, refused.Fa);

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);

        var current = target.App is { } existing ? await CurrentAsync(existing.Id, requested, ct) : [];
        var (settled, settleError) = Settle(requested, current, context, executing: false);
        if (settleError is { } notSettled) return AgentValidation.Refused(notSettled.En, notSettled.Fa);

        if (target.App is { } app)
        {
            // Asked with the values masked: the service checks keys, and a handle is never resolved for a card.
            var refusals = await configuration.ValidateSetEnvAsync(ActorOf(context), app.Id,
                settled.Select(v => new AppEnvVarInput(v.Key, AgentSecretHandles.Masked, v.Secret, v.AvailableAtBuild)).ToList(), ct);
            if (refusals.Count > 0) return Refused(refusals);
        }

        var slug = target.App?.Slug ?? target.Planned!.Name;
        var detailEn = new List<string>();
        var detailFa = new List<string>();
        foreach (var variable in settled)
        {
            var (en, fa) = variable.Source switch
            {
                Source.Handle => ($"{variable.Key} (secret — {AgentSecretHandles.Masked}, entered with the lock button)",
                    $"{variable.Key} (محرمانه — {AgentSecretHandles.Masked}، واردشده با دکمهٔ قفل)"),
                Source.Generated => ($"{variable.Key} (secret — a random value is generated on the server)",
                    $"{variable.Key} (محرمانه — مقداری تصادفی روی سرور ساخته می‌شود)"),
                _ => ($"{variable.Key} = {Shown(variable.Value, "(empty)")}", $"{variable.Key} = {Shown(variable.Value, "(خالی)")}")
            };
            if (variable.AvailableAtBuild) { en += ", also at build"; fa += "، هنگام build هم"; }
            if (variable.Existed) { en += ", replaces the current value"; fa += "، مقدار فعلی را جایگزین می‌کند"; }
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
            TitleEn: settled.Count == 1
                ? $"Set environment variable {settled[0].Key} on {slug}"
                : $"Set {settled.Count} environment variables on {slug}",
            TitleFa: settled.Count == 1
                ? $"تنظیم متغیر محیطی {settled[0].Key} روی {slug}"
                : $"تنظیم {settled.Count} متغیر محیطی روی {slug}",
            DetailEn: string.Join("; ", detailEn),
            DetailFa: string.Join("؛ ", detailFa),
            WarningsEn: ["Takes effect on the app's next deploy."],
            WarningsFa: ["با استقرار بعدی اپ اعمال می‌شود."]);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (requested, error) = Read(args);
        if (error is { } refused) return NotPossible(refused.En, refused.Fa);

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        // Asked again, against the rows as they are now: a variable may have become a secret since the card.
        var (settled, settleError) = Settle(requested, await CurrentAsync(app.Id, requested, ct), context, executing: true);
        if (settleError is { } notSettled) return NotPossible(notSettled.En, notSettled.Fa);

        var inputs = new List<AppEnvVarInput>();
        foreach (var variable in settled)
        {
            var value = variable.Source switch
            {
                Source.Handle => vault.Resolve(context.WorkspaceId, context.UserId, variable.Value!.Trim()),
                Source.Generated => ServiceCredentials.Generate(GeneratedLength),
                _ => variable.Value
            };
            if (value is null) return NotPossible(Expired(variable.Key).En, Expired(variable.Key).Fa);
            inputs.Add(new AppEnvVarInput(variable.Key, value, variable.Secret, variable.AvailableAtBuild));
        }

        var outcome = await configuration.SetEnvAsync(ActorOf(context), app.Id, inputs, ct);
        if (!outcome.Ok)
        {
            // The service's refusals name keys, not values, today; a secret is scrubbed out regardless, so a
            // refusal that ever quotes one cannot carry it into the result the model reads.
            var secrets = inputs.Where(i => i.IsSecret && !string.IsNullOrEmpty(i.Value)).Select(i => i.Value!).ToList();
            string Scrub(string text) =>
                secrets.Aggregate(text, (scrubbed, secret) => scrubbed.Replace(secret, AgentSecretHandles.Masked, StringComparison.Ordinal));
            return Failed(outcome.Refusals.Select(r => r with { Message = Scrub(r.Message), MessageFa = Scrub(r.MessageFa) }).ToList());
        }
        var changes = outcome.Value!;
        var generated = settled.Where(v => v.Source == Source.Generated).Select(v => v.Key).ToHashSet(StringComparer.Ordinal);

        string LineEn(AppEnvVarChange c) =>
            $"{c.Key} ({(generated.Contains(c.Key) ? "generated secret, " : c.IsSecret ? "secret, " : "")}{(c.Created ? "new" : "replaced")})";
        string LineFa(AppEnvVarChange c) =>
            $"{c.Key} ({(generated.Contains(c.Key) ? "رمز ساخته‌شده، " : c.IsSecret ? "محرمانه، " : "")}{(c.Created ? "جدید" : "جایگزین شد")})";

        return new AgentToolResult(true,
            AgentJson.Serialize(new
            {
                app = app.Slug,
                variables = changes.Select(c => new
                {
                    key = c.Key, created = c.Created, secret = c.IsSecret, availableAtBuild = c.AvailableAtBuild,
                    value = c.IsSecret ? AgentSecretHandles.Masked : null,
                    generated = generated.Contains(c.Key) ? true : (bool?)null
                }),
                appliesOn = "next deploy"
            }),
            $"Set {string.Join(", ", changes.Select(LineEn))} on '{app.Slug}'. It takes effect on the app's next deploy.",
            $"{string.Join("، ", changes.Select(LineFa))} روی «{app.Slug}» تنظیم شد. با استقرار بعدی اپ اعمال می‌شود.",
            Links: [AppLink(app)]);
    }

    // ---- reading the call ----

    private enum Source { Plain, Handle, Generated }

    /// <param name="Value">A plain value, or the handle text; null for a generated one.</param>
    /// <param name="Secret">What the call said, or null when it left the flag out.</param>
    private sealed record Requested(string Key, string? Value, Source Source, bool? Secret, bool? AvailableAtBuild);

    /// <summary>A variable with every flag decided against what the app holds now.</summary>
    private sealed record Settled(string Key, string? Value, Source Source, bool Secret, bool AvailableAtBuild, bool Existed);

    /// <summary>
    /// The variables as given, the same key twice collapsed to its last (as the service would apply them).
    /// Values are taken as given — not trimmed: a value's spaces are its own business — and a key that could
    /// never be set in a container (blank, with "=" or whitespace in it) is refused here rather than stored
    /// and found broken at deploy time.
    /// </summary>
    private static (IReadOnlyList<Requested> Variables, (string En, string Fa)? Error) Read(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("variables", out var list)
            || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return ([], ("Give at least one variable to set, as variables: [{ key, value }].",
                "دست‌کم یک متغیر برای تنظیم بدهید، به شکل variables: [{ key, value }]."));

        var variables = new List<Requested>();
        foreach (var item in list.EnumerateArray())
        {
            var key = AgentArgs.String(item, "key");
            if (key is null) return ([], ("Every variable needs a key.", "هر متغیر به نام (key) نیاز دارد."));
            if (key.Any(c => c == '=' || char.IsWhiteSpace(c) || char.IsControl(c)))
                return ([], ($"'{key}' cannot be a variable name: names contain no '=' and no spaces.",
                    $"«{key}» نمی‌تواند نام متغیر باشد: نام متغیر «=» یا فاصله ندارد."));

            var generate = AgentArgs.Bool(item, "generate") ?? false;
            string? value = null;
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var raw)
                && raw.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (raw.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    return ([], ($"Give {key} a text value.", $"برای {key} مقدار متنی بدهید."));
                value = raw.ValueKind == JsonValueKind.String ? raw.GetString() ?? "" : raw.GetRawText();
            }

            if (generate && value is not null)
                return ([], ($"Give {key} either a value or generate: true, not both.",
                    $"برای {key} یا مقدار بدهید یا generate: true، نه هر دو."));
            if (!generate && value is null)
                return ([], ($"Give a value for {key} (it may be empty), or generate: true for a random secret.",
                    $"برای {key} مقدار بدهید (می‌تواند خالی باشد)، یا برای رمز تصادفی generate: true را بدهید."));

            var source = generate ? Source.Generated : AgentSecretHandles.IsHandle(value) ? Source.Handle : Source.Plain;

            // "Bearer {{secret:…}}" would be stored with the handle text in it — and the handle is not the secret.
            if (source == Source.Plain && AgentSecretHandles.Pattern.IsMatch(value!))
                return ([], ($"A {HandleShape} handle has to be {key}'s whole value; it cannot be part of a longer text.",
                    $"دستهٔ {HandleShape} باید کل مقدار {key} باشد و نمی‌تواند بخشی از متنی بلندتر باشد."));

            variables.Add(new Requested(key, value, source, AgentArgs.Bool(item, "secret"), AgentArgs.Bool(item, "availableAtBuild")));
        }

        return (variables.GroupBy(v => v.Key, StringComparer.Ordinal).Select(g => g.Last()).ToList(), null);
    }

    /// <summary>The flags the app's variables carry now, for the keys this call names.</summary>
    private async Task<Dictionary<string, (bool Secret, bool AvailableAtBuild)>> CurrentAsync(
        Guid appId, IReadOnlyList<Requested> requested, CancellationToken ct)
    {
        var keys = requested.Select(v => v.Key).ToList();
        return await db.EnvironmentVariables.AsNoTracking()
            .Where(e => e.AppId == appId && keys.Contains(e.Key))
            .ToDictionaryAsync(e => e.Key, e => (e.IsSecret, e.AvailableAtBuild), StringComparer.Ordinal, ct);
    }

    /// <summary>
    /// Every flag decided: what the call said, else what the row holds now, else plain and runtime-only. A
    /// handle and a generated value are always secret. Refuses a secret turned plain, a plain value for a
    /// secret, and a handle the vault does not know (or no longer knows) — the last asked of the vault
    /// without the value ever leaving it.
    /// </summary>
    private (IReadOnlyList<Settled> Variables, (string En, string Fa)? Error) Settle(
        IReadOnlyList<Requested> requested, IReadOnlyDictionary<string, (bool Secret, bool AvailableAtBuild)> current,
        AgentToolContext context, bool executing)
    {
        var settled = new List<Settled>();
        foreach (var variable in requested)
        {
            var exists = current.TryGetValue(variable.Key, out var now);

            // A handle or a generated value is a secret by what it is; "secret: false" beside one contradicts
            // the call itself, and is said to be that — not mistaken for a request to make the variable plain.
            if (exists && now.Secret && variable.Secret == false && variable.Source != Source.Plain)
                return ([], (
                    $"{variable.Key}: secret: false contradicts the value — {(variable.Source == Source.Handle ? "one entered with the lock button" : "a generated one")} is always a secret. Leave secret out (or pass true) and call again.",
                    $"{variable.Key}: secret: false با مقدار ناسازگار است — {(variable.Source == Source.Handle ? "مقداری که با دکمهٔ قفل وارد شده" : "مقدار ساخته‌شده")} همیشه محرمانه است. secret را ندهید (یا true بدهید) و دوباره درخواست کنید."));

            if (exists && now.Secret && variable.Secret == false)
                return ([], ($"{variable.Key} is a secret on this app, and the assistant does not turn a secret into plain text. Leave secret out to keep it secret, or change it in the panel.",
                    $"{variable.Key} روی این اپ محرمانه است و دستیار متغیر محرمانه را به متن معمولی تبدیل نمی‌کند. secret را ندهید تا محرمانه بماند، یا آن را در پنل تغییر دهید."));

            var secret = variable.Source != Source.Plain || (variable.Secret ?? (exists && now.Secret));
            if (secret && variable.Source == Source.Plain)
                return ([], PlainSecret(variable.Key, offerGenerate: true));

            if (variable.Source == Source.Handle
                && vault.Resolve(context.WorkspaceId, context.UserId, variable.Value!.Trim()) is null)
                return ([], executing ? Expired(variable.Key) : UnknownHandle(variable.Key));

            settled.Add(new Settled(variable.Key, variable.Value, variable.Source, secret,
                variable.AvailableAtBuild ?? (exists && now.AvailableAtBuild), exists));
        }
        return (settled, null);
    }

    private static string Shown(string? value, string empty) => value switch
    {
        null or "" => empty,
        { Length: > ShownValueLength } lengthy => lengthy[..ShownValueLength] + "…",
        var text => text
    };
}
