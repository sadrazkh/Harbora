using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Verification;
using Harbora.Shared.Inspection;

namespace Harbora.Tests;

/// <summary>
/// The launches the API's tests and the CLI's tests both talk about: one with a certain Postgres, a
/// maybe-Redis and one of each kind of variable. Shaped the way <c>LaunchService.BuildProposalAsync</c>
/// builds one, including what its inspection carries and its fields do not.
/// </summary>
internal static class LaunchScript
{
    /// <summary>On the example file's line for a question: what a page never draws, and an answer must not send.</summary>
    public const string ExampleValueOfAQuestion = "sk_live_committed_by_mistake";

    public static IReadOnlyList<LaunchStepView> Steps(
        LaunchStepState source = LaunchStepState.Done, LaunchStepState services = LaunchStepState.Waiting,
        LaunchStepState app = LaunchStepState.Waiting, LaunchStepState deploy = LaunchStepState.Waiting,
        LaunchStepState verify = LaunchStepState.Waiting, string? deployDetail = null, string? deployLink = null) =>
    [
        new(LaunchStepKeys.Source, "Reading your code", "خواندن کد", source),
        new(LaunchStepKeys.Services, "Databases", "دیتابیس‌ها", services),
        new(LaunchStepKeys.App, "Creating the app", "ساختن اپ", app),
        new(LaunchStepKeys.Deploy, "Deploying", "استقرار", deploy, deployDetail, null, deployLink),
        new(LaunchStepKeys.Verify, "Checking that it works", "بررسی سالم بودن", verify)
    ];

    public static LaunchView View(
        LaunchStatus status,
        Guid id,
        Guid? appId = null,
        string? appName = null,
        string? publicUrl = null,
        Guid? deploymentId = null,
        string? failure = null,
        IReadOnlyList<LaunchStepView>? steps = null,
        IReadOnlyList<AppCheck>? checks = null,
        bool canRetry = false,
        LaunchSourceKind source = LaunchSourceKind.Upload,
        bool appGone = false,
        IReadOnlyList<LaunchCreatedThing>? created = null) =>
        new(id, status, source, "shop", appId, appName, publicUrl, deploymentId, failure, failure is null ? null : "شکست خورد",
            steps ?? Steps(), checks ?? [], canRetry,
            status is LaunchStatus.Succeeded or LaunchStatus.SucceededWithProblems or LaunchStatus.Failed
                or LaunchStatus.InspectFailed or LaunchStatus.Abandoned,
            appGone, created);

    public static SourceInspection Inspection(
        IReadOnlyList<string>? roots = null, string? root = null) =>
        new(DetectedStack.Node, DetectedAppKind.Web, root, roots ?? [], 3000, null, "npx prisma migrate deploy",
            [
                new DetectedService(DetectedServiceKind.Postgres, [new ServiceEvidence("package.json", "\"pg\" in dependencies")], true),
                new DetectedService(DetectedServiceKind.Redis, [new ServiceEvidence(".env.example", "REDIS_URL named")], false)
            ],
            [
                new DetectedEnvVar("DATABASE_URL", EnvVarDisposition.ProvidedByAttach, "postgres://u:example-attach-password@localhost/db", DetectedServiceKind.Postgres, null, ".env.example"),
                new DetectedEnvVar("DB_HOST", EnvVarDisposition.AliasOfAttach, "example-alias-host", DetectedServiceKind.Postgres, EnvAliasRole.Host, ".env.example"),
                new DetectedEnvVar("STRIPE_SECRET_KEY", EnvVarDisposition.NeedsAnswer, ExampleValueOfAQuestion, null, null, ".env.example"),
                new DetectedEnvVar("SUPPORT_EMAIL", EnvVarDisposition.NeedsAnswer, null, null, null, ".env.example"),
                new DetectedEnvVar("JWT_SECRET", EnvVarDisposition.GeneratedSecret, null, null, null, ".env.example"),
                new DetectedEnvVar("LOG_LEVEL", EnvVarDisposition.KeepExample, "info", null, null, ".env.example")
            ],
            [new InspectionNote("env-file-ignored", "A real .env was left out.", "فایل .env واقعی کنار گذاشته شد.")]);

    public static LaunchProposal Proposal(Guid id, SourceInspection? inspection = null, IReadOnlyList<LaunchEnvField>? fields = null)
    {
        var read = inspection ?? Inspection();
        IReadOnlyList<LaunchEnvField> env = fields ??
        [
            new("DB_HOST", EnvVarDisposition.AliasOfAttach, null, false, ".env.example", ManagedServiceType.PostgreSql),
            new("STRIPE_SECRET_KEY", EnvVarDisposition.NeedsAnswer, null, true, ".env.example"),
            new("SUPPORT_EMAIL", EnvVarDisposition.NeedsAnswer, null, false, ".env.example"),
            new("JWT_SECRET", EnvVarDisposition.GeneratedSecret, null, true, ".env.example"),
            new("LOG_LEVEL", EnvVarDisposition.KeepExample, "info", false, ".env.example"),
            new("DATABASE_URL", EnvVarDisposition.ProvidedByAttach, null, false, ".env.example", ManagedServiceType.PostgreSql)
        ];

        return new LaunchProposal(
            id,
            read,
            new LaunchChoices
            {
                Name = "shop",
                Kind = ServiceKind.Web,
                ContainerPort = 3000,
                ReleaseCommand = read.ReleaseCommand,
                RootDirectory = read.RootDirectory,
                InstanceSizeKey = "small",
                Services = [new LaunchServiceChoice(ManagedServiceType.PostgreSql, Aliases: [new AttachEnvAlias("DB_HOST", "PGHOST")])],
                EnvVars = env.Where(f => f.Disposition is not (EnvVarDisposition.AliasOfAttach or EnvVarDisposition.ProvidedByAttach))
                    .Select(f => new LaunchEnvChoice(f.Key, f.SuggestedValue, f.IsSecret)).ToList()
            },
            "shop",
            env,
            [
                new LaunchServiceOption(ManagedServiceType.PostgreSql, "PostgreSQL", "پستگرس", read.Services[0]),
                new LaunchServiceOption(ManagedServiceType.Redis, "Redis", "ردیس", read.Services[1]),
                new LaunchServiceOption(ManagedServiceType.MongoDb, "MongoDB", "مونگو", null)
            ]);
    }

    public static readonly LaunchQuote Charged = new(
        [new LaunchQuoteLine("App shop", "اپ shop", 1_000), new LaunchQuoteLine("Database shop-postgresql", "دیتابیس", 200)],
        FirstHourMinor: 1_200, BalanceMinor: 30_000, "USD", Exempt: false, Refusal: null);
}
