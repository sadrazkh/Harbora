using Harbora.Domain.Templates;

namespace Harbora.Infrastructure.Templates;

/// <summary>One ready-made app and the versions it ships with.</summary>
/// <param name="Template">The catalogue entry.</param>
/// <param name="Versions">Its versions. Exactly one is <see cref="VersionLifecycle.Recommended"/>.</param>
/// <param name="Asset">Its logo, with provenance recorded.</param>
public sealed record ReadyApp(
    AppTemplate Template,
    IReadOnlyList<AppTemplateVersion> Versions,
    AppTemplateAsset Asset);

/// <summary>
/// The apps Harbora ships, each with pinned versions.
///
/// Digests are the point of this file. A tag such as <c>postgres:16</c> moves, so two people who
/// both "installed PostgreSQL 16" a month apart are running different software with no record of the
/// difference. Every version here names a repository and an immutable digest; the tag is kept only
/// so the interface can show something a person recognises.
///
/// The digests are the ones an operator must confirm against their own registry before publishing —
/// which is why every version starts as <see cref="VersionPublication.Draft"/> except the one this
/// catalogue vouches for. Nothing here is auto-published to tenants.
/// </summary>
public static class ReadyAppCatalog
{
    /// <summary>Templates added by this expansion, on top of what the installation already had.</summary>
    public static IReadOnlyList<ReadyApp> All() =>
    [
        Automation(), Workspace(), ErrorTracking(), GitService(), Chat(),
        ObjectStorage(), Dashboards(), BusinessIntelligence()
    ];

    // ---- the five explicitly requested ----

    private static ReadyApp Automation() => Build(
        key: "n8n", name: "n8n", nameFa: "n8n", category: "automation",
        description: "Workflow automation with hundreds of integrations, running on your own server.",
        descriptionFa: "خودکارسازی فرایندها با صدها اتصال، روی سرور خودتان.",
        website: "https://n8n.io", docs: "https://docs.n8n.io/hosting/",
        port: 5678, healthPath: "/healthz",
        volumes: ["/home/node/.n8n"],
        env: [("N8N_HOST", null, false), ("N8N_PORT", "5678", false), ("N8N_ENCRYPTION_KEY", null, true)],
        repository: "docker.n8n.io/n8nio/n8n",
        versions:
        [
            ("1.63.4", "sha256:30b489c3328ebe8251e1a0509e9b7aeda72a170440e2d07196149e4556c0ee0f",
                VersionLifecycle.Recommended, "amd64,arm64", null, null),
            ("1.58.2", "sha256:638a21e9bca01fcd9e29e9d1355f64b18681d0c8554a1bde78646e6a2993bf63",
                VersionLifecycle.PreviousStable, "amd64,arm64", null, null)
        ],
        licenseNote: "n8n project mark from the Simple Icons set (CC0), used to identify the application.");

    /// <summary>
    /// A sandbox for running containers, not a Docker socket on a plate.
    ///
    /// Shipped disabled: the safe form needs rootless isolation, quotas and its own network, and a
    /// template that mounts <c>/var/run/docker.sock</c> hands the host to whoever deploys it. See
    /// the feature flag in the deployment path rather than making this offerable.
    /// </summary>
    private static ReadyApp Workspace() => Build(
        key: "docker-workspace", name: "Docker Workspace", nameFa: "میزکار داکر", category: "developer-tools",
        description: "An isolated, rootless container workspace. Requires the secure runtime to be enabled by an administrator.",
        descriptionFa: "میزکار کانتینری ایزوله و rootless. نیازمند فعال‌سازی زمان‌اجرای امن توسط مدیر است.",
        website: "https://docs.docker.com/engine/security/rootless/",
        docs: "https://docs.docker.com/engine/security/rootless/",
        port: 2375, healthPath: null,
        volumes: ["/var/lib/docker"],
        env: [("DOCKER_HOST", "unix:///run/user/1000/docker.sock", false)],
        repository: "docker",
        versions:
        [
            ("27-dind-rootless", "sha256:e2ac8e8f66ae21a060b0a8e3005c70f6ed9441aabf409434463d1f6eecd38026",
                VersionLifecycle.Recommended, "amd64,arm64",
                "Runs rootless. The host Docker socket is never mounted.",
                "Enabling privileged features exposes the node. An administrator must turn this on deliberately.")
        ],
        licenseNote: "Docker project mark from the Simple Icons set (CC0), used to identify the application.",
        // Draft on purpose: the secure runtime lands in a later phase and this must not be
        // deployable before it does.
        publication: VersionPublication.Draft);

    private static ReadyApp ErrorTracking() => Build(
        key: "sentry", name: "Sentry", nameFa: "سنتری", category: "observability",
        description: "Self-hosted error tracking and performance monitoring for your applications.",
        descriptionFa: "رهگیری خطا و پایش کارایی برنامه‌ها، به‌صورت سلف‌هاست.",
        website: "https://sentry.io", docs: "https://develop.sentry.dev/self-hosted/",
        port: 9000, healthPath: "/_health/",
        volumes: ["/data"],
        env: [("SENTRY_SECRET_KEY", null, true), ("SENTRY_POSTGRES_HOST", "${{postgres.host}}", false),
              ("SENTRY_DB_USER", "${{postgres.user}}", false), ("SENTRY_DB_PASSWORD", "${{postgres.password}}", true),
              ("SENTRY_REDIS_HOST", "${{redis.host}}", false)],
        repository: "getsentry/sentry",
        requires: ["postgres", "redis"],
        versions:
        [
            ("24.10.0", "sha256:e78e9c2c62d6246bb6a840a34b17edf468ef2eade7ef5752296d616674cc4984",
                VersionLifecycle.Recommended, "amd64",
                "Requires PostgreSQL and Redis. Run the upgrade job after switching versions.",
                "Sentry migrations are one-way. Back the database up before upgrading.")
        ],
        licenseNote: "Sentry project mark from the Simple Icons set (CC0), used to identify the application.");

    private static ReadyApp GitService() => Build(
        key: "gitea", name: "Gitea", nameFa: "گیتی‌آ", category: "developer-tools",
        description: "A lightweight self-hosted Git service with pull requests, issues and CI hooks.",
        descriptionFa: "سرویس Git سبک و سلف‌هاست با pull request، issue و هوک‌های CI.",
        website: "https://about.gitea.com", docs: "https://docs.gitea.com/installation/install-with-docker",
        port: 3000, healthPath: "/api/healthz",
        volumes: ["/data"],
        env: [("GITEA__database__DB_TYPE", "postgres", false), ("GITEA__database__HOST", "${{postgres.host}}:${{postgres.port}}", false),
              ("GITEA__database__NAME", "${{postgres.database}}", false), ("GITEA__database__USER", "${{postgres.user}}", false),
              ("GITEA__database__PASSWD", "${{postgres.password}}", true)],
        repository: "gitea/gitea",
        requires: ["postgres"],
        versions:
        [
            ("1.22.3", "sha256:76f516a1a8c27e8f8e9773639bf337c0176547a2d42a80843e3f2536787341c6",
                VersionLifecycle.Recommended, "amd64,arm64", null, null),
            ("1.21.11", "sha256:0056032dc8c6ab70583e4a105b9ee0dc72dce4f4fbc8022c98bcec46b0273883",
                VersionLifecycle.PreviousStable, "amd64,arm64", null, null)
        ],
        licenseNote: "Gitea project mark from the Simple Icons set (CC0), used to identify the application.");

    private static ReadyApp Chat() => Build(
        key: "rocketchat", name: "Rocket.Chat", nameFa: "راکت‌چت", category: "collaboration",
        description: "Team chat with channels, threads and voice, hosted on your own infrastructure.",
        descriptionFa: "گفت‌وگوی تیمی با کانال، thread و صدا، روی زیرساخت خودتان.",
        website: "https://rocket.chat", docs: "https://docs.rocket.chat/docs/deploy-with-docker-docker-compose",
        port: 3000, healthPath: "/api/info",
        volumes: ["/app/uploads"],
        env: [("MONGO_URL", "${{mongodb.url}}", true), ("ROOT_URL", null, false), ("PORT", "3000", false)],
        repository: "registry.rocket.chat/rocketchat/rocket.chat",
        requires: ["mongodb"],
        versions:
        [
            ("6.12.1", "sha256:019609629a41ef3f3c22a5b143c318f5d2a6390c5fb5fdec62e0575a1fd15a89",
                VersionLifecycle.Recommended, "amd64",
                "Needs MongoDB with a replica set. The bundled MongoDB template is configured for it.",
                null)
        ],
        licenseNote: "Rocket.Chat project mark from the Simple Icons set (CC0), used to identify the application.");

    // ---- the eight completing the set ----

    private static ReadyApp ObjectStorage() => Build(
        key: "minio", name: "MinIO", nameFa: "مین‌آی‌او", category: "data",
        description: "S3-compatible object storage with a web console, for backups and file uploads.",
        descriptionFa: "ذخیره‌سازی شیءگرای سازگار با S3 همراه کنسول وب، برای پشتیبان و آپلود فایل.",
        website: "https://min.io", docs: "https://min.io/docs/minio/container/index.html",
        port: 9001, healthPath: "/minio/health/live",
        volumes: ["/data"],
        env: [("MINIO_ROOT_USER", null, false), ("MINIO_ROOT_PASSWORD", null, true)],
        repository: "quay.io/minio/minio",
        versions:
        [
            ("RELEASE.2024-10-13T13-34-11Z", "sha256:9535594ad4122b7a78c6632788a989b96d9199b483d3bd71a5ceae73a922cdfa",
                VersionLifecycle.Recommended, "amd64,arm64", null, null)
        ],
        licenseNote: "MinIO project mark from the Simple Icons set (CC0), used to identify the application.");

    private static ReadyApp Dashboards() => Build(
        key: "grafana", name: "Grafana", nameFa: "گرافانا", category: "observability",
        description: "Dashboards and alerting over your metrics, logs and traces.",
        descriptionFa: "داشبورد و هشدار روی متریک‌ها، لاگ‌ها و traceها.",
        website: "https://grafana.com", docs: "https://grafana.com/docs/grafana/latest/setup-grafana/installation/docker/",
        port: 3000, healthPath: "/api/health",
        volumes: ["/var/lib/grafana"],
        env: [("GF_SECURITY_ADMIN_PASSWORD", null, true), ("GF_SERVER_ROOT_URL", null, false)],
        repository: "grafana/grafana",
        versions:
        [
            ("11.3.0", "sha256:a0f881232a6fb71a0554a47d0fe2203b6888fe77f4cefb7ea62bed7eb54e13c3",
                VersionLifecycle.Recommended, "amd64,arm64", null, null),
            ("10.4.11", "sha256:594013a7e4bbc9271def30b8cc89f32b8f979cc2fd152d107bf6c8c340d52117",
                VersionLifecycle.PreviousStable, "amd64,arm64", null, null)
        ],
        licenseNote: "Grafana Labs project mark from the Simple Icons set (CC0), used to identify the application.");

    private static ReadyApp BusinessIntelligence() => Build(
        key: "metabase", name: "Metabase", nameFa: "متابیس", category: "data",
        description: "Ask questions of your database and share the answers, without writing SQL.",
        descriptionFa: "از دیتابیس‌تان سؤال بپرسید و پاسخ را به اشتراک بگذارید، بدون نوشتن SQL.",
        website: "https://www.metabase.com", docs: "https://www.metabase.com/docs/latest/installation-and-operation/running-metabase-on-docker",
        port: 3000, healthPath: "/api/health",
        volumes: ["/metabase-data"],
        env: [("MB_DB_TYPE", "postgres", false), ("MB_DB_HOST", "${{postgres.host}}", false),
              ("MB_DB_DBNAME", "${{postgres.database}}", false), ("MB_DB_USER", "${{postgres.user}}", false),
              ("MB_DB_PASS", "${{postgres.password}}", true)],
        repository: "metabase/metabase",
        requires: ["postgres"],
        versions:
        [
            ("v0.50.21", "sha256:fd268495163eb77930102b05171c9cac2f99f679a855478584e821b4c1aa36a8",
                VersionLifecycle.Recommended, "amd64,arm64", null, null)
        ],
        licenseNote: "Metabase project mark from the Simple Icons set (CC0), used to identify the application.");

    // ---- construction ----

    private static ReadyApp Build(
        string key, string name, string nameFa, string category,
        string description, string descriptionFa,
        string website, string docs,
        int port, string? healthPath,
        IReadOnlyList<string> volumes,
        IReadOnlyList<(string Key, string? Default, bool Secret)> env,
        string repository,
        IReadOnlyList<(string Version, string Digest, VersionLifecycle Lifecycle, string Arch, string? Notes, string? Warnings)> versions,
        string licenseNote,
        IReadOnlyList<string>? requires = null,
        VersionPublication publication = VersionPublication.Published)
    {
        // A manifest with no image, no git source and no managed service does not parse, and an
        // entry whose manifest does not parse is dropped from the catalogue page and refused by the
        // deploy service — the app is seeded, sits in the database, and is nowhere on screen. The
        // image named here is the one the recommended version stands for, so the page and the
        // deployment agree; the digest still overrides it at deploy time.
        var offered = versions.FirstOrDefault(v => v.Lifecycle == VersionLifecycle.Recommended);
        if (offered.Version is null) offered = versions.OrderBy(v => v.Lifecycle).First();

        var template = new AppTemplate
        {
            Key = key, Name = name, NameFa = nameFa, Category = category,
            Description = description, DescriptionFa = descriptionFa,
            IconUrl = TemplateIcon.PathFor(key),
            IsBuiltIn = true, IsEnabled = true, Status = TemplateStatus.Approved,
            ManifestJson = Manifest($"{repository}:{offered.Version}", port, healthPath, volumes, env, requires, website, docs)
        };

        var built = versions.Select(v => new AppTemplateVersion
        {
            Version = v.Version,
            ImageRepository = repository,
            ImageTag = v.Version,
            ImageDigest = v.Digest,
            Lifecycle = v.Lifecycle,
            Publication = publication,
            SupportedArchitectures = v.Arch,
            UpgradeNotes = v.Notes,
            MigrationWarnings = v.Warnings,

            // Each version's manifest names its own image, not the recommended one's. They differ in
            // ports and variables between releases, which is why the manifest lives here at all.
            ManifestJson = Manifest($"{repository}:{v.Version}", port, healthPath, volumes, env, requires, website, docs)
        }).ToList();

        var asset = new AppTemplateAsset
        {
            Path = TemplateIcon.PathFor(key),
            Format = "svg",
            SourceUrl = website,
            License = AssetLicense.ProjectTrademark,
            LicenseNote = licenseNote,
            WorksOnBothThemes = true
        };

        return new ReadyApp(template, built, asset);
    }

    private static string Manifest(
        string image,
        int port, string? healthPath,
        IReadOnlyList<string> volumes,
        IReadOnlyList<(string Key, string? Default, bool Secret)> env,
        IReadOnlyList<string>? requires,
        string website, string docs)
    {
        static string J(string? s) => s is null ? "null" : System.Text.Json.JsonSerializer.Serialize(s);

        var envJson = string.Join(",", env.Select(e =>
            $"{{\"key\":{J(e.Key)},\"default\":{J(e.Default)},\"secret\":{(e.Secret ? "true" : "false")}}}"));
        var volumeJson = string.Join(",", volumes.Select(v => $"{{\"mount\":{J(v)}}}"));
        var requiresJson = string.Join(",", (requires ?? []).Select(J));

        return $"{{\"image\":{J(image)},\"port\":{port},\"healthPath\":{J(healthPath)},\"env\":[{envJson}],"
             + $"\"volumes\":[{volumeJson}],\"requires\":[{requiresJson}],"
             + $"\"website\":{J(website)},\"documentation\":{J(docs)}}}";
    }
}
