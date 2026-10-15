using System.Globalization;
using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The agent's own platform settings, as the settings page shows them.
/// </summary>
/// <param name="Model">What the owner typed, trimmed; blank means "use the assistant's model".</param>
/// <param name="FallbackModels">Normalised: trimmed, blanks and repeats gone, comma-joined.</param>
/// <param name="DailyRequestLimitText">What the box shows: empty when the default applies.</param>
/// <param name="DailyRequestLimit">The number that applies: the default when blank or unreadable, 0 for unlimited.</param>
/// <param name="ConversationRetentionDaysText">What the box shows: empty when conversations are kept for ever.</param>
/// <param name="ConversationRetentionDays">The number that applies: 0 — keep for ever — when blank, 0 or unreadable.</param>
public sealed record AgentSettings(
    string Model,
    string FallbackModels,
    string DailyRequestLimitText,
    int DailyRequestLimit,
    string ConversationRetentionDaysText = "",
    int ConversationRetentionDays = 0);

/// <summary>The settings after validation, ready to store.</summary>
public sealed record AgentSettingsInput(
    string Model, string FallbackModels, string DailyRequestLimit, string ConversationRetentionDays = "");

/// <summary>Either the cleaned-up values, or why they were refused — in both languages.</summary>
public sealed record AgentSettingsValidation(AgentSettingsInput? Input, string? Error, string? ErrorFa)
{
    public bool Ok => Input is not null;
}

/// <summary>The models a call will ask for: the primary, then the fallbacks in order.</summary>
public sealed record AgentModelPlan(string? Primary, IReadOnlyList<string> Fallbacks);

/// <summary>
/// Reads and writes <see cref="AgentSettingKeys"/>. Validation is a separate, pure step so a form that
/// also saves other settings (the settings page saves the assistant's provider and key in the same
/// POST) can refuse a bad value before it has stored anything, rather than half-saving.
/// </summary>
public sealed class AgentSettingsService(HarboraDbContext db)
{
    private static readonly string[] Keys =
    [
        AgentSettingKeys.Model, AgentSettingKeys.FallbackModels, AgentSettingKeys.DailyRequestLimit,
        AgentSettingKeys.ConversationRetentionDays
    ];

    /// <summary>The longest retention the box accepts: ten years.</summary>
    public const int MaxConversationRetentionDays = 3650;

    public async Task<AgentSettings> GetAsync(CancellationToken ct)
    {
        // IgnoreQueryFilters like AssistantService: these are platform settings, not a workspace's.
        var stored = await db.Settings.IgnoreQueryFilters()
            .Where(s => Keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var limitText = stored.GetValueOrDefault(AgentSettingKeys.DailyRequestLimit)?.Trim() ?? "";
        var retentionText = stored.GetValueOrDefault(AgentSettingKeys.ConversationRetentionDays)?.Trim() ?? "";
        return new AgentSettings(
            Model: stored.GetValueOrDefault(AgentSettingKeys.Model)?.Trim() ?? "",
            FallbackModels: stored.GetValueOrDefault(AgentSettingKeys.FallbackModels)?.Trim() ?? "",
            DailyRequestLimitText: limitText,
            DailyRequestLimit: EffectiveLimit(limitText),
            ConversationRetentionDaysText: retentionText,
            ConversationRetentionDays: EffectiveRetentionDays(retentionText));
    }

    /// <summary>
    /// Stores values that came back from <see cref="Validate"/>. Takes the validated type, not three
    /// strings, so nothing unchecked can reach the table through here.
    /// </summary>
    public async Task SaveAsync(AgentSettingsInput input, CancellationToken ct)
    {
        await SetAsync(AgentSettingKeys.Model, input.Model, ct);
        await SetAsync(AgentSettingKeys.FallbackModels, input.FallbackModels, ct);
        await SetAsync(AgentSettingKeys.DailyRequestLimit, input.DailyRequestLimit, ct);
        await SetAsync(AgentSettingKeys.ConversationRetentionDays, input.ConversationRetentionDays, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Checks what the owner typed. A model id never contains whitespace, and a pasted
    /// "vendor/model, other/model" with a stray space inside one id would otherwise be stored and fail
    /// at the provider with its own, less helpful, wording. The limit must be a whole number of at
    /// least zero; blank means the default, and Persian digits are read as the digits they are. The
    /// retention is checked the same way: blank or 0 keeps conversations for ever, otherwise 1 to
    /// <see cref="MaxConversationRetentionDays"/> days.
    /// </summary>
    public static AgentSettingsValidation Validate(
        string? model, string? fallbackModels, string? dailyRequestLimit, string? conversationRetentionDays = null)
    {
        var primary = (model ?? "").Trim();
        if (!IsModelId(primary, allowBlank: true))
            return Refuse(
                "The agent model must be one model id, such as vendor/model:free — no spaces.",
                "مدل عامل باید یک شناسهٔ مدل باشد، مثلاً vendor/model:free — بدون فاصله.");

        var fallbacks = new List<string>();
        foreach (var part in (fallbackModels ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsModelId(part, allowBlank: false))
                return Refuse(
                    $"\"{part}\" is not a model id. Separate fallback models with commas; an id has no spaces.",
                    $"«{part}» شناسهٔ مدل نیست. مدل‌های جایگزین را با ویرگول جدا کنید؛ شناسه فاصله ندارد.");

            if (!fallbacks.Contains(part, StringComparer.OrdinalIgnoreCase)) fallbacks.Add(part);
        }

        var limitText = AsciiDigits((dailyRequestLimit ?? "").Trim());
        var storedLimit = "";
        if (limitText.Length > 0)
        {
            // NumberStyles.None: digits only. "-5", "+5", "1,000" and "1.5" are all refused, not
            // quietly turned into something else.
            if (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
                return Refuse(
                    "The daily request limit must be a whole number, 0 or more (0 means unlimited).",
                    "سقف درخواست روزانه باید یک عدد صحیح ۰ یا بیشتر باشد (۰ یعنی نامحدود).");

            storedLimit = limit.ToString(CultureInfo.InvariantCulture);
        }

        var retentionText = AsciiDigits((conversationRetentionDays ?? "").Trim());
        var storedRetention = "";
        if (retentionText.Length > 0)
        {
            if (!int.TryParse(retentionText, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
                || days > MaxConversationRetentionDays)
                return Refuse(
                    $"Keep conversations must be a whole number of days from 1 to {MaxConversationRetentionDays} — or blank or 0 to keep them for ever.",
                    $"نگه‌داری گفت‌وگوها باید عددی صحیح از {PersianDigits(1)} تا {PersianDigits(MaxConversationRetentionDays)} روز باشد — یا خالی یا {PersianDigits(0)} برای نگه‌داشتن برای همیشه.");

            storedRetention = days.ToString(CultureInfo.InvariantCulture);
        }

        return new AgentSettingsValidation(
            new AgentSettingsInput(primary, string.Join(',', fallbacks), storedLimit, storedRetention), null, null);
    }

    /// <summary>
    /// Decides which models a call asks for. The agent's own model wins; blank falls back to the
    /// assistant's. A fallback that repeats the primary (or another fallback) is dropped — asking the
    /// same model twice is not a fallback.
    /// </summary>
    public static AgentModelPlan Resolve(string? agentModel, string? assistantModel, string? fallbackModels)
    {
        var primary = !string.IsNullOrWhiteSpace(agentModel) ? agentModel.Trim()
            : !string.IsNullOrWhiteSpace(assistantModel) ? assistantModel.Trim()
            : null;

        var fallbacks = new List<string>();
        foreach (var part in (fallbackModels ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(part, primary, StringComparison.OrdinalIgnoreCase)) continue;
            if (!fallbacks.Contains(part, StringComparer.OrdinalIgnoreCase)) fallbacks.Add(part);
        }

        return new AgentModelPlan(primary, fallbacks);
    }

    /// <summary>
    /// The number a stored value means. Blank, or anything that is not a non-negative whole number
    /// (a hand-edited row), is the default — never zero, because zero means "no limit at all".
    /// </summary>
    public static int EffectiveLimit(string? stored) =>
        int.TryParse(AsciiDigits((stored ?? "").Trim()), NumberStyles.None, CultureInfo.InvariantCulture, out var limit)
            ? limit
            : AgentSettingKeys.DefaultDailyRequestLimit;

    /// <summary>
    /// The retention a stored value means, in days; 0 keeps every conversation. Anything the page would
    /// not have accepted (a hand-edited row) is 0 too — the opposite default to the limit's, because the
    /// wrong guess here deletes what the owner meant to keep, and that cannot be undone.
    /// </summary>
    public static int EffectiveRetentionDays(string? stored) =>
        int.TryParse(AsciiDigits((stored ?? "").Trim()), NumberStyles.None, CultureInfo.InvariantCulture, out var days)
        && days <= MaxConversationRetentionDays
            ? days
            : 0;

    private static bool IsModelId(string value, bool allowBlank) =>
        value.Length == 0 ? allowBlank : value.Length <= 200 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    /// <summary>Persian (۰-۹) and Arabic-Indic (٠-٩) digits to 0-9; everything else untouched.</summary>
    private static string AsciiDigits(string text)
    {
        if (text.Length == 0) return text;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = c switch
                {
                    >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                    >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                    _ => c
                };
            }
        });
    }

    /// <summary>A number written in Persian digits (۰-۹), for a Persian sentence built from a constant.</summary>
    private static string PersianDigits(int value) =>
        string.Concat(value.ToString(CultureInfo.InvariantCulture).Select(c => (char)('۰' + (c - '0'))));

    private static AgentSettingsValidation Refuse(string error, string errorFa) => new(null, error, errorFa);

    private async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var setting = await db.Settings.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) db.Settings.Add(new Setting { Key = key, Value = value });
        else setting.Value = value;
    }
}
