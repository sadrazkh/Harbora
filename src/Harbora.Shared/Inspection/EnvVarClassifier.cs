namespace Harbora.Shared.Inspection;

/// <summary>Says what each name in an example file means, so that the confirm screen asks only what a person has to answer.</summary>
internal static class EnvVarClassifier
{
    /// <summary>Set by the platform itself, whatever the example says. Not even shown.</summary>
    private static readonly HashSet<string> PlatformOwned = new(StringComparer.Ordinal)
    {
        "PORT", "NODE_ENV", "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "PYTHONUNBUFFERED"
    };

    /// <summary>
    /// Exact names only. <c>STRIPE_SECRET_KEY</c> has <c>SECRET</c> in it and generating one would
    /// produce something Stripe has never heard of; what is listed here is read by the app alone.
    /// </summary>
    private static readonly HashSet<string> GeneratedSecrets = new(StringComparer.Ordinal)
    {
        "SECRET", "SECRET_KEY", "APP_SECRET", "APP_KEY", "JWT_SECRET", "JWT_KEY", "JWT_SECRET_KEY",
        "SESSION_SECRET", "SESSION_KEY", "COOKIE_SECRET", "ENCRYPTION_KEY", "AUTH_SECRET", "NEXTAUTH_SECRET",
        "DJANGO_SECRET_KEY", "FLASK_SECRET_KEY", "TOKEN_SECRET", "ACCESS_TOKEN_SECRET", "REFRESH_TOKEN_SECRET",
        "SALT", "HASH_SALT"
    };

    private static readonly string[] CredentialWords = ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASS", "API", "CREDENTIAL"];

    /// <param name="ownServices">False for a compose stack: its names belong to the services it starts, so neither attach rule applies.</param>
    public static (IReadOnlyList<DetectedEnvVar> Vars, bool DatabaseUnclear) Classify(
        IReadOnlyList<ExampleVar> vars, IReadOnlyList<DetectedService> services, bool ownServices)
    {
        var result = new List<DetectedEnvVar>();
        var unclear = false;

        foreach (var example in vars)
        {
            if (PlatformOwned.Contains(example.Name)) continue;
            result.Add(ClassifyOne(example, services, ownServices, ref unclear));
        }

        return (result, unclear);
    }

    private static DetectedEnvVar ClassifyOne(
        ExampleVar example, IReadOnlyList<DetectedService> services, bool ownServices, ref bool unclear)
    {
        var name = example.Name;
        var value = example.Value.Length == 0 ? null : example.Value;

        DetectedEnvVar Var(EnvVarDisposition disposition, DetectedServiceKind? service = null, EnvAliasRole? role = null) =>
            new(name, disposition, disposition == EnvVarDisposition.GeneratedSecret ? null : value, service, role, example.File);

        if (ownServices)
        {
            var owners = services.Where(s => AttachEnvNames.All[s.Kind].Contains(name)).ToList();
            if (owners.Count > 0)
            {
                // Two engines both setting DATABASE_URL leave the app with whichever attached last.
                if (owners.Count > 1) unclear = true;
                return Var(EnvVarDisposition.ProvidedByAttach, OwnerOf(owners, example.Value));
            }

            if (EnvAliases.Specific.TryGetValue(name, out var alias) && services.Any(s => s.Kind == alias.Kind))
                return Var(EnvVarDisposition.AliasOfAttach, alias.Kind, alias.Role);

            if (EnvAliases.Generic.TryGetValue(name, out var role))
            {
                if (TheOneDatabase(services) is { } database)
                    return Var(EnvVarDisposition.AliasOfAttach, database, role);

                // Which engine is the person's to say. Which part of a connection this is is already
                // known, so ticking one is enough to answer it.
                unclear = true;
                return Var(EnvVarDisposition.NeedsAnswer, role: role);
            }

            // DATABASE_URL with no scheme to go on is the same question as DB_HOST. With one that is
            // not a server at all (sqlite:///…) it says plainly which database it is, and stays.
            if (name == "DATABASE_URL" && IsPlaceholder(example.Value)) unclear = true;
        }

        if (GeneratedSecrets.Contains(name)) return Var(EnvVarDisposition.GeneratedSecret);

        return NeedsAPerson(name, example.Value) ? Var(EnvVarDisposition.NeedsAnswer) : Var(EnvVarDisposition.KeepExample);
    }

    private static DetectedServiceKind OwnerOf(List<DetectedService> owners, string value)
    {
        if (owners.Count == 1) return owners[0].Kind;

        var scheme = EnvAliases.SchemeOf(value);
        return (owners.FirstOrDefault(o => EnvAliases.SchemeConfirms(o.Kind, scheme))
                ?? owners.FirstOrDefault(o => o.Certain)
                ?? owners[0]).Kind;
    }

    /// <summary>A weak guess does not make a second engine out of a certain one.</summary>
    private static DetectedServiceKind? TheOneDatabase(IReadOnlyList<DetectedService> services)
    {
        var databases = services.Where(s => s.Kind is DetectedServiceKind.Postgres or DetectedServiceKind.MySql
            or DetectedServiceKind.MariaDb or DetectedServiceKind.MongoDb).ToList();

        var certain = databases.Where(s => s.Certain).ToList();
        var pool = certain.Count > 0 ? certain : databases;
        return pool.Count == 1 ? pool[0].Kind : null;
    }

    private static bool NeedsAPerson(string name, string value) =>
        CredentialWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)) ||
        IsPlaceholder(value) ||
        NamesThisMachine(value);

    /// <summary>A development address, which on a server would be the container talking to itself.</summary>
    private static bool NamesThisMachine(string value) =>
        value.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("127.0.0.1", StringComparison.Ordinal) ||
        value.Contains("::1", StringComparison.Ordinal);

    private static bool IsPlaceholder(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return true;
        if (text[0] == '<' && text[^1] == '>') return true;

        // Another variable's value: nothing here would expand it, so it would arrive as these literal characters.
        if (text.Contains("${", StringComparison.Ordinal)) return true;
        if (text.Contains("...", StringComparison.Ordinal) || text.Contains('…')) return true;

        var lower = text.ToLowerInvariant();
        return lower is "todo" ||
               lower.StartsWith("your_", StringComparison.Ordinal) ||
               lower.StartsWith("your-", StringComparison.Ordinal) ||
               lower.Contains("changeme", StringComparison.Ordinal) ||
               lower.Contains("change_me", StringComparison.Ordinal) ||
               lower.Contains("change-me", StringComparison.Ordinal) ||
               lower.Contains("xxx", StringComparison.Ordinal) ||
               lower.Contains("example", StringComparison.Ordinal);
    }
}
