using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a failed deployment tells the user.
///
/// Every one of these failures used to produce the single sentence "Container failed its health
/// check." It is accurate for all four and useful for none: the container that exited, the one that
/// never started, the one that was removed by something else, and the one that simply never answered
/// its health path all need different next steps.
/// </summary>
public class HealthDiagnosisTests
{
    private const string Container = "harbora-shop-7";

    private static string Explain(HealthReport report) => HealthDiagnosis.Explain(report, Container);

    [Fact]
    public void A_container_that_exited_reports_the_exit_and_its_own_last_words()
    {
        var report = new HealthReport(HealthFailure.Exited, "Exited (1) 2 seconds ago",
            "Error: database is uninitialized and superuser password is not specified");

        var message = Explain(report);

        message.Should().Contain("exited");
        message.Should().Contain("Exited (1)", "the exit code is the first thing anyone asks for");
        message.Should().Contain("superuser password is not specified",
            "the container already said what was wrong — repeating it saves the user a hunt");
    }

    [Fact]
    public void A_crash_looping_container_is_described_as_crashing_not_as_silent()
    {
        // What a startup crash actually looks like on this platform: containers run under
        // unless-stopped, so Docker revives them and the state reads "restarting", never "exited".
        // Reported live by a Postgres image started without POSTGRES_PASSWORD.
        var report = new HealthReport(HealthFailure.CrashLooping, "Restarting (1) 3 seconds ago",
            "Error: Database is uninitialized and superuser password is not specified.");

        var message = Explain(report);

        message.Should().Contain("crashing").And.Contain("Restarting (1)");
        message.Should().Contain("superuser password is not specified");
        message.Should().NotContain("never returned a success response",
            "that wording sends the user to check ports and health paths that are not the problem");
    }

    [Fact]
    public void A_container_with_no_output_says_so_rather_than_trailing_off()
    {
        var message = Explain(new HealthReport(HealthFailure.Exited, "Exited (137) 1 second ago", ""));

        message.Should().Contain("Exited (137)").And.Contain("no output");
    }

    [Fact]
    public void An_unanswered_health_path_names_the_url_that_was_probed()
    {
        var report = new HealthReport(HealthFailure.NoHealthyResponse, "Up 40 seconds",
            ProbeUrl: "http://harbora-shop-7:3000/healthz");

        var message = Explain(report);

        message.Should().Contain("http://harbora-shop-7:3000/healthz");
        message.Should().Contain("running", "the container is alive — the app inside it is the problem");
        message.Should().NotContain("exited", "sending the user to look for a crash would waste their time");
    }

    [Fact]
    public void A_container_that_never_started_is_not_described_as_a_crash()
    {
        var message = Explain(new HealthReport(HealthFailure.NeverStarted, "Created"));

        message.Should().Contain("never reached the running state");
        message.Should().NotContain("exited");
    }

    [Fact]
    public void A_container_removed_by_something_else_points_outside_this_deployment()
    {
        // Distinct from a crash: nothing in the image is wrong, so telling someone to check their
        // environment variables sends them looking in the wrong place entirely.
        var message = Explain(new HealthReport(HealthFailure.Vanished));

        message.Should().Contain(Container).And.Contain("disappeared");
        message.Should().NotContain("environment variable");
    }

    [Fact]
    public void A_long_log_keeps_the_end_because_that_is_where_the_crash_is()
    {
        var noise = new string('x', HealthDiagnosis.MaxTailChars * 2);
        var report = new HealthReport(HealthFailure.Exited, "Exited (1) ago", noise + "FATAL: out of memory");

        var message = Explain(report);

        message.Should().Contain("FATAL: out of memory", "truncating from the end would drop the cause");
        message.Length.Should().BeLessThan(noise.Length, "the field is not a log viewer");
    }

    [Fact]
    public void The_error_survives_the_advice_that_follows_it()
    {
        // Taken from the live failure: the cause is one line, followed by ~600 characters of helpful
        // suggestions and a documentation link. A window that only fits the suggestions reports
        // everything except the thing that went wrong.
        var log =
            "Error: Database is uninitialized and superuser password is not specified.\n" +
            "       You must specify POSTGRES_PASSWORD to a non-empty value for the superuser.\n" +
            new string('.', 500) + "\n" +
            "       You may also use POSTGRES_HOST_AUTH_METHOD=trust to allow all connections\n" +
            "       without a password. This is *not* recommended.\n" +
            "       See PostgreSQL documentation about trust:\n" +
            "       https://www.postgresql.org/docs/current/auth-trust.html";

        var message = Explain(new HealthReport(HealthFailure.CrashLooping, "Restarting (1)", log));

        message.Should().Contain("Database is uninitialized and superuser password is not specified");
    }

    [Fact]
    public void A_healthy_report_is_not_a_failure()
    {
        HealthReport.Healthy.IsHealthy.Should().BeTrue();
        new HealthReport(HealthFailure.Exited).IsHealthy.Should().BeFalse();
    }
}
