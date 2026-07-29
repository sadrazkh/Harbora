using FluentAssertions;
using Harbora.Web.Infrastructure;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Output of <c>harbora info</c>. This is what a locked-out operator pastes into a bug report or a
/// support chat, so it must diagnose the problem without ever exposing a live credential.
/// </summary>
public class AdminDiagnosticsTests
{
    // ---- master key: the usual reason the panel won't start after an update ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_master_key_is_named_as_the_cause(string? key)
    {
        var described = AdminDiagnostics.DescribeMasterKey(key);

        described.Should().Contain("MISSING");
        described.Should().Contain("harbora fix-key", "the message has to say what to do next");
    }

    [Fact]
    public void The_insecure_development_key_is_called_out()
    {
        var described = AdminDiagnostics.DescribeMasterKey("dev-insecure-master-key-change-me");

        described.Should().Contain("INSECURE");
        described.Should().Contain("harbora fix-key");
    }

    [Fact]
    public void A_real_master_key_is_never_printed()
    {
        const string secret = "Zm9vYmFyYmF6cXV1eDEyMzQ1Njc4OTBhYmNkZWY=";

        var described = AdminDiagnostics.DescribeMasterKey(secret);

        described.Should().NotContain(secret, "printing the key would defeat the point of having one");
        described.Should().Contain("set");
    }

    // ---- connection string ----

    [Fact]
    public void The_database_password_is_redacted()
    {
        const string connection = "Host=postgres;Port=5432;Database=harbora;Username=harbora;Password=s3cr3t-pw";

        var redacted = AdminDiagnostics.RedactConnectionString(connection);

        redacted.Should().NotContain("s3cr3t-pw");
        redacted.Should().Contain("Password=***");
    }

    [Fact]
    public void The_rest_of_the_connection_string_survives_redaction()
    {
        // Host and database name are exactly what makes the output useful for diagnosis.
        var redacted = AdminDiagnostics.RedactConnectionString(
            "Host=postgres;Port=5432;Database=harbora;Username=harbora;Password=x");

        redacted.Should().Contain("Host=postgres");
        redacted.Should().Contain("Database=harbora");
        redacted.Should().Contain("Username=harbora");
    }

    [Theory]
    [InlineData("password=lower")]
    [InlineData("PASSWORD=UPPER")]
    [InlineData("Password = spaced")]
    [InlineData("pwd=aliased")]
    public void Password_variants_npgsql_accepts_are_all_redacted(string fragment)
    {
        var redacted = AdminDiagnostics.RedactConnectionString($"Host=db;{fragment};Database=harbora");

        redacted.Should().NotContain("lower").And.NotContain("UPPER")
            .And.NotContain("spaced").And.NotContain("aliased");
        redacted.Should().Contain("***");
    }

    [Fact]
    public void A_password_in_the_final_position_is_still_redacted()
    {
        // No trailing semicolon to anchor on — an easy place for a redactor to give up.
        var redacted = AdminDiagnostics.RedactConnectionString("Host=db;Username=u;Password=trailing");

        redacted.Should().NotContain("trailing");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_connection_string_reports_not_set(string? connection)
    {
        AdminDiagnostics.RedactConnectionString(connection).Should().Be("(not set)");
    }

    [Fact]
    public void A_username_containing_the_word_password_is_not_mangled()
    {
        // Guards against a redactor so greedy it destroys the diagnostic value of the output.
        var redacted = AdminDiagnostics.RedactConnectionString("Host=db;Username=passworduser;Password=x");

        redacted.Should().Contain("Username=passworduser");
    }
}
