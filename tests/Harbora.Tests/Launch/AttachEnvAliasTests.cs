using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Services;
using Harbora.Shared.Inspection;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Launch;

/// <summary>
/// The names an app really reads. <c>MONGO_URI</c> and <c>DB_HOST</c> reach it as extra entries computed
/// with the rest of the attach variables, so a password rotation reaches them too, and an app's own
/// variable still outranks every one of them.
/// </summary>
public sealed class AttachEnvAliasTests
{
    private const string Password = "s3cret-password-value";
    private static readonly ServiceCreds Creds = new("harbora-svc-x", 1234, "the-user", Password, "the_db");
    private readonly PassthroughProtector _protector = new();

    public static TheoryData<ManagedServiceType, EnvAliasRole> EveryEngineAndRole()
    {
        var data = new TheoryData<ManagedServiceType, EnvAliasRole>();
        foreach (var type in Enum.GetValues<ManagedServiceType>())
            foreach (var role in Enum.GetValues<EnvAliasRole>())
                data.Add(type, role);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryEngineAndRole))]
    public void Every_answer_is_a_key_that_really_exists_and_really_holds_that_part(ManagedServiceType type, EnvAliasRole role)
    {
        var key = AttachEnvRoles.SourceKeyFor(type, role);
        var env = ServiceCatalog.All[type].AttachEnv(Creds);

        if (key is null)
        {
            // Null is only honest when the engine has no such part to offer.
            env.Values.Should().NotContain(role switch
            {
                EnvAliasRole.User => Creds.User,
                EnvAliasRole.Database => Creds.Database,
                _ => throw new InvalidOperationException($"{type} has a {role}: every engine does.")
            });
            return;
        }

        env.Should().ContainKey(key);
        var value = env[key];
        switch (role)
        {
            case EnvAliasRole.Url:
                value.Should().Contain("://").And.Contain(Creds.Host);
                break;
            case EnvAliasRole.Host:
                value.Should().Be(Creds.Host);
                break;
            case EnvAliasRole.Port:
                value.Should().Be(Creds.Port.ToString());
                break;
            case EnvAliasRole.User:
                value.Should().Be(Creds.User);
                break;
            case EnvAliasRole.Password:
                value.Should().Be(Password);
                break;
            case EnvAliasRole.Database:
                value.Should().Be(Creds.Database);
                break;
        }
    }

    [Fact]
    public void Every_inspected_service_has_an_engine()
    {
        foreach (var kind in Enum.GetValues<DetectedServiceKind>())
            ServiceCatalog.All.Should().ContainKey(AttachEnvRoles.TypeFor(kind));
    }

    private AppManagedService Attachment(string? aliasesJson, ManagedServiceType type = ManagedServiceType.PostgreSql, string password = Password) => new()
    {
        Alias = "MAIN",
        EnvAliasesJson = aliasesJson,
        ManagedService = new ManagedService
        {
            Name = "main", Type = type, ContainerName = "harbora-svc-main", Username = "harbora", DatabaseName = "main",
            EncryptedPassword = _protector.Protect(password)
        }
    };

    private static string Json(params AttachEnvAlias[] aliases) => ManagedServiceAttachEnv.WriteAliases(aliases)!;

    [Fact]
    public void An_alias_carries_its_sources_value_unprefixed_and_a_plain_value_stays_plain()
    {
        var entries = ManagedServiceAttachEnv.EntriesFor(
            Attachment(Json(new AttachEnvAlias("DB_HOST", "PGHOST"))), _protector);

        var alias = entries.Single(e => e.Key == "DB_HOST");
        alias.Value.Should().Be("harbora-svc-main");
        alias.IsSecret.Should().BeFalse();
        entries.Should().NotContain(e => e.Key == "MAIN_DB_HOST", "the bare name is what the app reads");
    }

    [Fact]
    public void An_alias_of_a_secret_is_secret_and_stays_ciphertext_like_its_source()
    {
        var entries = ManagedServiceAttachEnv.EntriesFor(
            Attachment(Json(new AttachEnvAlias("POSTGRES_URL", "DATABASE_URL"), new AttachEnvAlias("DB_PASS", "PGPASSWORD"))), _protector);

        var url = entries.Single(e => e.Key == "POSTGRES_URL");
        url.IsSecret.Should().BeTrue();
        _protector.Unprotect(url.Value).Should().Contain(Password);

        var password = entries.Single(e => e.Key == "DB_PASS");
        password.IsSecret.Should().BeTrue();
        _protector.Unprotect(password.Value).Should().Be(Password);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"Name\":\"X\"}")]
    [InlineData("[{\"Name\":123}]")]
    [InlineData("[null]")]
    [InlineData("[{\"Name\":\"\",\"SourceKey\":\"PGHOST\"}]")]
    [InlineData("[{\"Name\":\"HAS SPACE\",\"SourceKey\":\"PGHOST\"}]")]
    [InlineData("[{\"Name\":\"X\"}]")]
    [InlineData("[{\"Name\":\"X\",\"SourceKey\":\"NOT_A_KEY_THIS_ENGINE_SETS\"}]")]
    [InlineData("")]
    [InlineData(null)]
    public void A_malformed_value_is_ignored_and_never_an_exception_at_deploy_time(string? json)
    {
        var plain = ManagedServiceAttachEnv.EntriesFor(Attachment(null), _protector).Select(e => e.Key);

        ManagedServiceAttachEnv.EntriesFor(Attachment(json), _protector).Select(e => e.Key).Should().BeEquivalentTo(plain);
    }

    [Fact]
    public void No_aliases_is_stored_as_nothing_at_all()
    {
        ManagedServiceAttachEnv.WriteAliases(null).Should().BeNull();
        ManagedServiceAttachEnv.WriteAliases([]).Should().BeNull();
    }

    private App AppWith(AppManagedService attachment, params EnvironmentVariable[] own)
    {
        var app = new App { Name = "a", Slug = "a" };
        app.ManagedServices.Add(attachment);
        foreach (var variable in own) app.EnvironmentVariables.Add(variable);
        return app;
    }

    [Fact]
    public void Aliases_reach_the_one_merge_a_deploy_and_harbora_env_pull_both_read()
    {
        var effective = EffectiveEnvironmentBuilder.Compute(
            AppWith(Attachment(Json(new AttachEnvAlias("MONGO_URI", "DATABASE_URL")))), _protector, "");

        var alias = effective.Single(e => e.Key == "MONGO_URI");
        alias.IsSecret.Should().BeTrue();
        alias.Source.Should().Be(ConfigSource.Database);
        _protector.Unprotect(alias.Value).Should().Contain(Password);
    }

    [Fact]
    public void An_alias_never_outranks_the_apps_own_variable()
    {
        var effective = EffectiveEnvironmentBuilder.Compute(
            AppWith(Attachment(Json(new AttachEnvAlias("DB_HOST", "PGHOST"))),
                new EnvironmentVariable { Key = "DB_HOST", Value = "my-own-host" }),
            _protector, "");

        var entry = effective.Single(e => e.Key == "DB_HOST");
        entry.Value.Should().Be("my-own-host");
        entry.Source.Should().Be(ConfigSource.App);
    }

    [Fact]
    public void After_a_password_rotation_the_alias_carries_the_new_password()
    {
        var attachment = Attachment(Json(new AttachEnvAlias("DB_PASS", "PGPASSWORD"), new AttachEnvAlias("POSTGRES_URL", "DATABASE_URL")));
        var app = AppWith(attachment);

        string Read(string key) => _protector.Unprotect(
            EffectiveEnvironmentBuilder.Compute(app, _protector, "").Single(e => e.Key == key).Value);

        Read("DB_PASS").Should().Be(Password);

        // What ManagedServiceEngine.RotatePasswordAsync does to the row.
        attachment.ManagedService!.EncryptedPassword = _protector.Protect("rotated-password-value");

        Read("DB_PASS").Should().Be("rotated-password-value");
        Read("POSTGRES_URL").Should().Contain("rotated-password-value").And.NotContain(Password);
    }

    [Fact]
    public async Task Attaching_with_aliases_stores_them_and_a_caller_with_none_stores_nothing()
    {
        using var h = new DatabaseServiceHarness();
        var withAliases = h.SeedService("orders");
        var without = h.SeedService("reports");
        var app = h.SeedApp("shop");

        var first = await h.Attachments().AttachAsync(withAliases.Id, app.Id, null, null, null, default, [new AttachEnvAlias("DB_HOST", "PGHOST")]);
        var second = await h.Attachments().AttachAsync(without.Id, app.Id, null, null, null, default);

        first.Ok.Should().BeTrue();
        second.Ok.Should().BeTrue();
        var rows = await h.Db.AppManagedServices.AsNoTracking().ToListAsync();
        ManagedServiceAttachEnv.ReadAliases(rows.Single(r => r.ManagedServiceId == withAliases.Id).EnvAliasesJson)
            .Should().Equal(new AttachEnvAlias("DB_HOST", "PGHOST"));
        rows.Single(r => r.ManagedServiceId == without.Id).EnvAliasesJson.Should().BeNull();
    }
}
