namespace Harbora.Domain.Features;

/// <summary>
/// One sellable capability of the platform.
/// </summary>
/// <param name="Key">
/// Stable identifier. Stored on grants, named in <c>[RequireFeature]</c> attributes and in the
/// navigation map, so it is frozen the moment a row references it.
/// </param>
/// <param name="NameEn">Short name, English.</param>
/// <param name="NameFa">Short name, Persian.</param>
/// <param name="PitchEn">One line explaining what the customer is missing, English.</param>
/// <param name="PitchFa">The same line, Persian.</param>
/// <param name="Default">
/// What a workspace gets when nobody has decided anything about it. This is the shipped answer, and
/// for anything a provider would charge for it is <see cref="FeatureState.Locked"/> — a default of
/// Enabled means the feature is given away to every existing customer on the update that adds it.
/// </param>
public sealed record PlatformFeature(
    string Key,
    string NameEn,
    string NameFa,
    string PitchEn,
    string PitchFa,
    FeatureState Default)
{
    public string Name(bool isFa) => isFa ? NameFa : NameEn;
    public string Pitch(bool isFa) => isFa ? PitchFa : PitchEn;
}

/// <summary>
/// The catalogue of features an owner can grant, as code.
///
/// <para>
/// Deliberately not rows an operator invents: a key only means anything because something in the
/// codebase reads it, exactly like <c>Capabilities</c>. An operator-created "feature" would be a
/// name that gates nothing — a promise the platform has no way to keep.
/// </para>
/// </summary>
public static class PlatformFeatures
{
    /// <summary>Code written in the panel that runs without a repository or a Dockerfile.</summary>
    public const string Functions = "functions";

    public static readonly IReadOnlyList<PlatformFeature> All =
    [
        new(Functions,
            "Functions", "فانکشن‌ها",
            "Write code in the panel — HTTP endpoints, scheduled jobs and event handlers — with no repository, no Dockerfile and no git push.",
            "کد را در همین پنل بنویسید — اندپوینت HTTP، کار زمان‌بندی‌شده و هندلر رویداد — بدون ریپازیتوری، بدون Dockerfile و بدون git push.",
            FeatureState.Locked)
    ];

    public static PlatformFeature? Find(string? key) =>
        key is null ? null : All.FirstOrDefault(f => f.Key == key);

    /// <summary>
    /// The shipped state for a key. An unknown key is <see cref="FeatureState.Hidden"/> rather than
    /// enabled: a typo in an attribute or a grant left behind by a removed feature must fail closed.
    /// </summary>
    public static FeatureState DefaultFor(string key) =>
        Find(key)?.Default ?? FeatureState.Hidden;
}
