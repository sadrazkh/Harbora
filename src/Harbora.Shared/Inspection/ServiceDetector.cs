using System.Text.RegularExpressions;

namespace Harbora.Shared.Inspection;

/// <summary>Works out which managed services the app needs, and writes down why, so that a guess can be checked.</summary>
internal static class ServiceDetector
{
    private const string PrismaSchema = "prisma/schema.prisma";

    private static readonly Regex PrismaDatasource = new(@"^datasource\s+\w+\s*\{", RegexOptions.CultureInvariant);
    private static readonly Regex PrismaProvider = new(@"^provider\s*=\s*""([^""]+)""", RegexOptions.CultureInvariant);

    /// <summary>Any compose file, including <c>docker-compose.dev.yml</c>, which is where a project with a Dockerfile usually keeps its database.</summary>
    private static readonly Regex ComposeFile = new(
        @"^(?:docker-)?compose(?:\.[^/]+)?\.ya?ml$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ComposeImage = new(@"^\s*(?:-\s*)?image\s*:(.*)$", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, DetectedServiceKind> ComposeImages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["postgres"] = DetectedServiceKind.Postgres,
        ["mysql"] = DetectedServiceKind.MySql,
        ["mariadb"] = DetectedServiceKind.MariaDb,
        ["mongo"] = DetectedServiceKind.MongoDb,
        ["redis"] = DetectedServiceKind.Redis,
        ["rabbitmq"] = DetectedServiceKind.RabbitMq,
        ["nats"] = DetectedServiceKind.Nats,
        ["getmeili/meilisearch"] = DetectedServiceKind.Meilisearch
    };

    /// <summary>Everything found, a row per kind, in the enum's order so that the screen's list does not shuffle.</summary>
    public static IReadOnlyList<DetectedService> Detect(
        SourceTree app, ProjectFacts facts, IReadOnlyList<ExampleVar> example, List<InspectionNote> notes)
    {
        var found = new Findings();

        foreach (var dependency in facts.Dependencies)
        foreach (var kind in ServiceTable.KindsFor(dependency))
            found.Add(kind, dependency.File, dependency.Reason, dependency.Runtime);

        if (ServiceTable.UsesSqlite(facts)) notes.Add(InspectionNotes.SqliteNeedsVolume());
        if (ServiceTable.UsesSqlServer(facts)) notes.Add(InspectionNotes.SqlServerUnsupported());

        ReadPrisma(app, found, notes);
        ReadCompose(app, found);
        ReadExample(example, found, notes);

        return found.ToList();
    }

    private static void ReadPrisma(SourceTree app, Findings found, List<InspectionNote> notes)
    {
        if (app.ReadText(PrismaSchema) is not { } schema) return;

        // The generator block has a provider too, which is why this has to know where it is.
        var inDatasource = false;
        foreach (var raw in schema.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;

            if (PrismaDatasource.IsMatch(line)) inDatasource = true;
            else if (line.StartsWith('}')) inDatasource = false;
            else if (inDatasource && PrismaProvider.Match(line) is { Success: true } provider)
                AddEngine(provider.Groups[1].Value, $"datasource provider = \"{provider.Groups[1].Value}\"", PrismaSchema, found, notes);
        }
    }

    private static void ReadCompose(SourceTree app, Findings found)
    {
        foreach (var file in app.FileNames().Where(n => ComposeFile.IsMatch(n)).OrderBy(n => n, StringComparer.Ordinal))
        {
            if (app.ReadText(file) is not { } text) continue;

            foreach (var line in text.Split('\n'))
            {
                if (ComposeImage.Match(line.TrimEnd('\r')) is not { Success: true } match) continue;

                var image = ImageOf(match.Groups[1].Value.Trim());
                if (ComposeImages.TryGetValue(RepositoryOf(image), out var kind))
                    found.Add(kind, file, $"image: {image}", certain: true);
            }
        }
    }

    private static string ImageOf(string value)
    {
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        var image = (comment >= 0 ? value[..comment] : value).Trim().Trim('"', '\'');

        // ${POSTGRES_IMAGE:-postgres:16}: the default is the only part this can know.
        if (image.StartsWith("${", StringComparison.Ordinal) && image.EndsWith('}'))
        {
            var fallback = image.IndexOf(":-", StringComparison.Ordinal);
            if (fallback > 0) image = image[(fallback + 2)..^1];
        }

        return image;
    }

    private static string RepositoryOf(string image)
    {
        var repository = image;

        var digest = repository.IndexOf('@');
        if (digest >= 0) repository = repository[..digest];

        // The last colon is a tag only after the last slash; before it, it is a registry's port.
        var tag = repository.LastIndexOf(':');
        if (tag > repository.LastIndexOf('/')) repository = repository[..tag];

        foreach (var prefix in new[] { "docker.io/", "registry-1.docker.io/", "library/" })
        {
            if (repository.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) repository = repository[prefix.Length..];
        }

        return repository;
    }

    private static void ReadExample(IReadOnlyList<ExampleVar> example, Findings found, List<InspectionNote> notes)
    {
        foreach (var variable in example)
        {
            var scheme = EnvAliases.SchemeOf(variable.Value);

            // These reasons name the variable and the scheme and never the value: an example file's
            // DATABASE_URL often has somebody's real password in it, and this is shown on a page.
            if (EnvAliases.DatabaseUrlNames.Contains(variable.Name))
            {
                if (scheme is not null)
                    AddEngine(scheme, $"{variable.Name} holds a {scheme}:// address", variable.File, found, notes);
                continue;
            }

            if (variable.Name == "DB_CONNECTION")
            {
                AddEngine(variable.Value.Trim(), $"DB_CONNECTION={variable.Value.Trim()}", variable.File, found, notes);
                continue;
            }

            if (EnvAliases.ImpliedBy(variable.Name, scheme) is not { } kind) continue;

            var certain = EnvAliases.CertainByName.Contains(variable.Name) || EnvAliases.SchemeConfirms(kind, scheme);
            found.Add(kind, variable.File, $"{variable.Name} is named there", certain);
        }
    }

    /// <summary>What a Prisma provider, a Laravel <c>DB_CONNECTION</c> and a URL's scheme each call an engine, which is nearly the same list.</summary>
    private static void AddEngine(string name, string reason, string file, Findings found, List<InspectionNote> notes)
    {
        switch (name.ToLowerInvariant())
        {
            case "postgres" or "postgresql" or "pgsql":
                found.Add(DetectedServiceKind.Postgres, file, reason, certain: true);
                break;

            // MariaDB speaks the MySQL protocol and its URLs say mysql://, including the one Harbora
            // itself hands out. A project already known to be on MariaDB has not picked a second engine.
            case "mysql" or "mysql2":
                found.Add(found.Has(DetectedServiceKind.MariaDb) ? DetectedServiceKind.MariaDb : DetectedServiceKind.MySql,
                    file, reason, certain: true);
                break;

            case "mariadb":
                found.Add(DetectedServiceKind.MariaDb, file, reason, certain: true);
                break;

            case "mongodb" or "mongodb+srv":
                found.Add(DetectedServiceKind.MongoDb, file, reason, certain: true);
                break;

            case "sqlite":
                notes.Add(InspectionNotes.SqliteNeedsVolume());
                break;

            case "sqlserver" or "sqlsrv" or "mssql":
                notes.Add(InspectionNotes.SqlServerUnsupported());
                break;
        }
    }

    private sealed class Findings
    {
        // A handful of reasons is what a person reads beside a checkbox. A 1 MiB compose file naming
        // postgres fifty thousand times should not make a row, or a page, that long.
        private const int MaxEvidencePerKind = 8;

        private readonly Dictionary<DetectedServiceKind, (List<ServiceEvidence> Evidence, bool Certain)> _kinds = [];

        public bool Has(DetectedServiceKind kind) => _kinds.ContainsKey(kind);

        public void Add(DetectedServiceKind kind, string file, string reason, bool certain)
        {
            if (!_kinds.TryGetValue(kind, out var entry)) entry = ([], false);

            var evidence = new ServiceEvidence(file, reason);
            if (entry.Evidence.Count < MaxEvidencePerKind && !entry.Evidence.Contains(evidence)) entry.Evidence.Add(evidence);

            _kinds[kind] = (entry.Evidence, entry.Certain || certain);
        }

        public IReadOnlyList<DetectedService> ToList() =>
            _kinds.OrderBy(k => k.Key).Select(k => new DetectedService(k.Key, k.Value.Evidence, k.Value.Certain)).ToList();
    }
}
