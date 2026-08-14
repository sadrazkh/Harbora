using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Projects;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What copying an environment would create.
///
/// Every one of these is about a name. Two docker volumes with one name are one volume, and a copy
/// that reuses a container name takes over the container the original is serving from — so the plan
/// is checked against what is taken in the database <em>and</em> against what it has already handed
/// out to itself.
/// </summary>
public class ClonePlanTests
{
    private static CloneSourceApp App(
        string slug, string name = "App", long memory = 0, double cpu = 0, int domains = 0,
        ServiceKind kind = ServiceKind.Web, params string[] mounts) =>
        new(Guid.NewGuid(), name, slug, "small", memory, cpu, domains, kind,
            mounts.Select(m => new CloneSourceVolume(m, false, null)).ToList());

    private static CloneSourceService Service(
        string name, long memory = 0, double cpu = 0, bool hasDatabase = true) =>
        new(Guid.NewGuid(), name, "small", memory, cpu, hasDatabase);

    private static ClonePlan Plan(
        string desired = "Staging",
        IReadOnlyCollection<string>? envSlugs = null,
        IReadOnlyCollection<string>? appSlugs = null,
        IReadOnlyCollection<string>? containers = null,
        IReadOnlyList<CloneSourceApp>? apps = null,
        IReadOnlyList<CloneSourceService>? services = null) =>
        ClonePlan.Of(new CloneRequest(
            desired, Project, envSlugs ?? [], appSlugs ?? [], containers ?? [],
            apps ?? [], services ?? []));

    private static readonly Guid Project = Guid.CreateVersion7();

    // ---- the environment itself ----

    [Fact]
    public void The_environment_takes_a_dns_safe_slug_from_its_name() =>
        Plan("Staging Two!").EnvironmentSlug.Should().Be("staging-two");

    [Fact]
    public void A_slug_already_in_the_project_is_suffixed() =>
        Plan("Staging", envSlugs: ["staging", "staging-2"]).EnvironmentSlug.Should().Be("staging-3");

    [Fact]
    public void A_name_with_nothing_usable_in_it_still_gets_a_slug() =>
        Plan("!!!").EnvironmentSlug.Should().Be("environment");

    [Fact]
    public void The_name_is_what_was_typed_and_the_slug_is_what_survives_dns()
    {
        var plan = Plan("  Staging Two  ");

        plan.EnvironmentName.Should().Be("Staging Two");
        plan.EnvironmentSlug.Should().Be("staging-two");
    }

    // ---- applications ----

    [Fact]
    public void An_application_is_named_after_its_original_and_the_new_environment() =>
        Plan(apps: [App("api")]).Apps.Should().ContainSingle().Which.Slug.Should().Be("api-staging");

    [Fact]
    public void An_application_slug_already_in_the_workspace_is_suffixed() =>
        Plan(appSlugs: ["api-staging"], apps: [App("api")])
            .Apps[0].Slug.Should().Be("api-staging-2");

    [Fact]
    public void Two_originals_that_would_collide_do_not_both_get_the_same_name()
    {
        // "api" and "api-2" would both want "api-2-staging" once the first is suffixed.
        var plan = Plan(appSlugs: ["api-staging"], apps: [App("api"), App("api-2")]);

        plan.Apps.Select(a => a.Slug).Should().OnlyHaveUniqueItems(
            "the plan has to be unique against itself, not only against the database");
    }

    [Fact]
    public void No_two_applications_in_one_plan_ever_share_a_name()
    {
        // The rule is public and does not get to assume its caller deduplicated. Two entries with
        // one slug reaching the database is two containers with one name.
        var plan = Plan(apps: [App("api"), App("api"), App("api")]);

        plan.Apps.Select(a => a.Slug).Should().OnlyHaveUniqueItems();
        plan.Apps.SelectMany(a => a.Volumes).Select(v => v.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_taken_name_is_taken_even_when_it_was_written_in_another_case() =>
        Plan(appSlugs: ["API-STAGING"], apps: [App("api")])
            .Apps[0].Slug.Should().Be("api-staging-2",
                "docker does not distinguish them, so neither may this");

    [Fact]
    public void An_environment_slug_is_taken_regardless_of_case() =>
        Plan("Staging", envSlugs: ["STAGING"]).EnvironmentSlug.Should().Be("staging-2");

    [Fact]
    public void The_plan_says_which_project_the_copy_belongs_to() =>
        Plan().ProjectId.Should().Be(Project);

    [Fact]
    public void An_application_keeps_the_name_a_person_reads_and_only_the_slug_changes()
    {
        var plan = Plan(apps: [App("api", name: "Public API")]);

        plan.Apps[0].Name.Should().Be("Public API");
        plan.Apps[0].Slug.Should().Be("api-staging");
    }

    [Fact]
    public void A_volume_is_named_after_the_copy_not_after_the_original()
    {
        var plan = Plan(apps: [App("api", mounts: "/var/lib/data")]);

        plan.Apps[0].Volumes.Should().ContainSingle()
            .Which.Name.Should().Be("harbora-vol-api-staging-var-lib-data",
                "a volume named after the original is the original's volume, and the copy would " +
                "be writing into production's data");
    }

    [Fact]
    public void A_volume_keeps_its_mount_path_and_its_limits()
    {
        var plan = ClonePlan.Of(new CloneRequest("Staging", Project, [], [], [],
            [new CloneSourceApp(Guid.NewGuid(), "App", "api", "small", 0, 0, 0, ServiceKind.Web,
                [new CloneSourceVolume("/data", ReadOnly: true, SizeLimitBytes: 5_000)])],
            []));

        var volume = plan.Apps[0].Volumes[0];
        volume.MountPath.Should().Be("/data");
        volume.ReadOnly.Should().BeTrue();
        volume.SizeLimitBytes.Should().Be(5_000);
    }

    // ---- databases ----

    [Fact]
    public void A_database_gets_a_container_name_of_its_own()
    {
        var plan = Plan(services: [Service("Main DB")]);

        plan.Services[0].ContainerName.Should().Be("harbora-svc-main-db-staging");
        plan.Services[0].VolumeName.Should().Be("harbora-svc-main-db-staging-data");
    }

    [Fact]
    public void A_container_name_already_in_use_is_suffixed_and_the_database_name_follows_it()
    {
        var plan = Plan(containers: ["harbora-svc-cache-staging"], services: [Service("cache")]);

        plan.Services[0].ContainerName.Should().Be("harbora-svc-cache-staging-2");
        plan.Services[0].DatabaseName.Should().Be("cache_staging_2",
            "otherwise the second copy gets a container of its own and the first copy's database");
    }

    [Fact]
    public void An_engine_with_no_database_name_does_not_get_one_invented() =>
        Plan(services: [Service("cache", hasDatabase: false)])
            .Services[0].DatabaseName.Should().BeEmpty();

    [Fact]
    public void Two_databases_that_would_collide_get_distinct_containers()
    {
        var plan = Plan(containers: ["harbora-svc-db-staging"], services: [Service("db"), Service("DB")]);

        plan.Services.Select(s => s.ContainerName).Should().OnlyHaveUniqueItems();
    }

    // ---- what the whole package costs, and what it leaves behind ----

    [Fact]
    public void The_package_is_one_number_so_quota_is_answered_once()
    {
        var plan = Plan(
            apps: [App("api", memory: 512, cpu: 0.5), App("worker", memory: 256, cpu: 0.25)],
            services: [Service("db", memory: 1024, cpu: 1)]);

        plan.MemoryBytes.Should().Be(1792);
        plan.CpuCores.Should().Be(1.75);
        plan.ResourceCount.Should().Be(3);
    }

    [Fact]
    public void Domains_are_counted_rather_than_copied()
    {
        var plan = Plan(apps: [App("api", domains: 2), App("web", domains: 1)]);

        plan.DomainsLeftBehind.Should().Be(3,
            "the screen has to say how many are being left behind — a copy that silently drops " +
            "three hostnames is one somebody discovers a week later");
    }

    [Fact]
    public void An_empty_environment_plans_nothing() =>
        Plan().ResourceCount.Should().Be(0);

    // ---- the variables an attach owns ----

    [Fact]
    public void An_attach_owns_both_the_plain_name_and_its_prefixed_one()
    {
        var owned = ClonePlan.AttachOwnedKeys([("main db", new[] { "DATABASE_URL", "PGHOST" })]);

        owned.Should().BeEquivalentTo(
            ["DATABASE_URL", "PGHOST", "MAIN_DB_DATABASE_URL", "MAIN_DB_PGHOST"]);
    }

    [Fact]
    public void A_variable_no_attach_wrote_is_not_owned_by_one()
    {
        var owned = ClonePlan.AttachOwnedKeys([("db", new[] { "DATABASE_URL" })]);

        owned.Should().NotContain("APP_SECRET",
            "carrying the application's own configuration over is the point of copying it");
    }

    [Fact]
    public void Nothing_is_owned_when_there_is_nothing_to_attach() =>
        ClonePlan.AttachOwnedKeys([]).Should().BeEmpty();

    [Fact]
    public void An_application_is_attached_when_it_carries_the_services_own_prefix() =>
        ClonePlan.IsAttachedTo(["MAIN_DB_DATABASE_URL", "APP_SECRET"], "main db").Should().BeTrue();

    [Fact]
    public void An_application_holding_only_the_shared_names_is_not_read_as_attached_to_everything() =>
        ClonePlan.IsAttachedTo(["DATABASE_URL"], "main db").Should().BeFalse(
            "the unprefixed set belongs to whichever service claimed it first — reading it as " +
            "proof would report every application as attached to every database");

    [Fact]
    public void A_variable_that_merely_contains_the_prefix_is_not_proof_of_an_attach() =>
        ClonePlan.IsAttachedTo(["OLD_MAIN_DB_DATABASE_URL"], "main db").Should().BeFalse(
            "the attach writes its names at the front; a variable somebody renamed by hand is " +
            "their own configuration, and rewriting it would overwrite what they wrote");

    [Fact]
    public void Another_services_prefix_does_not_count() =>
        ClonePlan.IsAttachedTo(["CACHE_REDIS_URL"], "main db").Should().BeFalse();

    [Fact]
    public void An_application_with_no_variables_at_all_is_attached_to_nothing() =>
        ClonePlan.IsAttachedTo([], "db").Should().BeFalse();

    /// <summary>
    /// The plan carries each copy's kind, so the quota estimate can count the addresses the copy will
    /// consume.
    ///
    /// <para>
    /// This became load-bearing the moment cloned applications started being given an address. Before
    /// that they arrived with none, so leaving domains out of the clone's quota estimate was correct.
    /// Afterwards it was not, and the shape of the mistake is the dangerous one: nothing fails, a
    /// workspace already at its domain limit simply clones straight past it, and the limit is
    /// discovered to have been decorative.
    /// </para>
    /// </summary>
    [Fact]
    public void A_plan_carries_each_copys_kind_so_only_the_ones_that_get_an_address_are_counted()
    {
        var plan = ClonePlan.Of(new CloneRequest("Staging", Project, [], [], [],
            [App("web", kind: ServiceKind.Web),
             App("site", kind: ServiceKind.Static),
             App("worker", kind: ServiceKind.Worker),
             App("nightly", kind: ServiceKind.Cron)],
            []));

        plan.Apps.Count(a => Harbora.Infrastructure.Deployments.ServicePlan.CanHaveDomains(a.Kind))
            .Should().Be(2, "a worker and a cron take no inbound traffic, so neither consumes an address");
    }
}
