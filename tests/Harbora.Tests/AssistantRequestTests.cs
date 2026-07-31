using FluentAssertions;
using Harbora.Infrastructure.Assistant;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// When the assistant may be offered.
///
/// Asked in three places — the button, the endpoint behind it, and the settings screen — which is
/// exactly how a feature ends up hidden in the UI and reachable by POST.
/// </summary>
public class AssistantAvailabilityTests
{
    private static AssistantConfig Configured(
        bool enabled = true, string? provider = AssistantProviders.Anthropic,
        string? model = "claude-sonnet-5", string? key = "sk-test-key", string? baseUrl = null) =>
        new(enabled, provider, model, key, baseUrl);

    [Fact]
    public void Off_is_a_complete_answer()
    {
        AssistantAvailability.Check(Configured(enabled: false))!.Reason.Should().Contain("turned off");
    }

    [Fact]
    public void A_fully_configured_assistant_is_available()
    {
        AssistantAvailability.IsAvailable(Configured()).Should().BeTrue();
    }

    [Fact]
    public void An_unknown_provider_is_refused_rather_than_attempted()
    {
        // Typing "gpt" into a settings box must not produce a request to nowhere.
        AssistantAvailability.IsAvailable(Configured(provider: "gpt")).Should().BeFalse();
        AssistantAvailability.IsAvailable(Configured(provider: null)).Should().BeFalse();
    }

    [Fact]
    public void A_missing_model_is_refused()
    {
        AssistantAvailability.Check(Configured(model: " "))!.Reason.Should().Contain("model");
    }

    [Fact]
    public void A_missing_key_is_refused_for_a_provider_on_the_internet()
    {
        // Sending an unauthenticated request only produces somebody else's 401.
        AssistantAvailability.Check(Configured(key: null))!.Reason.Should().Contain("API key");
    }

    [Fact]
    public void A_model_running_on_this_machine_needs_no_key()
    {
        // The whole point of the local option: nothing leaves, so there is nobody to authenticate to.
        AssistantAvailability.IsAvailable(
            Configured(provider: AssistantProviders.OpenAiCompatible, key: null, baseUrl: "http://localhost:11434"))
            .Should().BeTrue();
    }

    [Fact]
    public void A_remote_host_that_merely_looks_local_still_needs_a_key()
    {
        // "localhost.evil.example" is a real trick, and this is the check that decides whether an
        // unauthenticated request goes out over the internet.
        AssistantAvailability.IsAvailable(
            Configured(key: null, baseUrl: "https://localhost.evil.example")).Should().BeFalse();
    }

    [Fact]
    public void Provider_names_are_not_case_sensitive()
    {
        AssistantAvailability.IsAvailable(Configured(provider: "Anthropic")).Should().BeTrue();
    }
}

/// <summary>
/// Turning a failed deployment into a question.
///
/// The text produced here is both what is shown to the person and what is sent, because a preview
/// that is assembled separately from the request is a preview of something else.
/// </summary>
public class AssistantRequestTests
{
    [Fact]
    public void The_log_that_travels_has_already_been_redacted()
    {
        var ask = AssistantRequest.ForFailedDeployment(
            "connecting postgres://app:hunter2secret@db/shop\nFATAL", null, "Web");

        ask.UserPrompt.Should().NotContain("hunter2secret");
        ask.Removed.Should().Be(1);
    }

    [Fact]
    public void A_secret_in_the_error_message_is_redacted_too()
    {
        // The error message is stored separately from the log and is just as likely to quote a
        // connection string back.
        var ask = AssistantRequest.ForFailedDeployment(
            "build ok", "could not connect: DB_PASSWORD=topsecretvalue", "Web");

        ask.UserPrompt.Should().NotContain("topsecretvalue");
        ask.Removed.Should().Be(1);
    }

    [Fact]
    public void Only_the_end_of_a_long_log_is_sent()
    {
        // The cause of a failure is at the end. Sending the front is both expensive and useless.
        var log = string.Join("\n", Enumerable.Range(0, 4000).Select(i => $"step {i} completed"))
                  + "\nERROR: the thing that actually broke";

        var ask = AssistantRequest.ForFailedDeployment(log, null, "Web");

        ask.Truncated.Should().BeTrue();
        ask.UserPrompt.Should().Contain("the thing that actually broke");
        ask.UserPrompt.Should().NotContain("step 0 completed");
    }

    [Fact]
    public void A_short_log_is_sent_whole_and_not_marked_truncated()
    {
        var ask = AssistantRequest.ForFailedDeployment("npm ERR! missing script: build", null, "Web");

        ask.Truncated.Should().BeFalse();
        ask.UserPrompt.Should().Contain("npm ERR! missing script: build");
    }

    [Fact]
    public void Truncation_cuts_at_a_line_boundary()
    {
        // Handing over half a line as though it were whole invites an explanation of a message that
        // was never printed.
        var log = new string('x', AssistantRequest.MaxLogCharacters) + "\ntail line\nlast line";

        var ask = AssistantRequest.ForFailedDeployment(log, null, null);

        ask.UserPrompt.Should().NotContain("xxx");
    }

    [Fact]
    public void A_deployment_with_no_log_says_so_rather_than_asking_about_nothing()
    {
        var ask = AssistantRequest.ForFailedDeployment(null, null, "Web");

        ask.UserPrompt.Should().Contain("no log");
        ask.Removed.Should().Be(0);
    }

    [Fact]
    public void The_instructions_carry_nothing_about_this_installation()
    {
        // The system prompt is fixed text. Anything about this server belongs in the redacted half.
        var ask = AssistantRequest.ForFailedDeployment("secret-hostname-internal", null, "Web");

        ask.SystemPrompt.Should().NotContain("secret-hostname-internal");
        ask.SystemPrompt.Should().Contain("redacted", "the model is told not to ask for what was removed");
    }
}
