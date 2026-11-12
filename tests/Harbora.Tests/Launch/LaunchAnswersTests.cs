using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Harbora.Tests.Launch.LaunchFixtures;

namespace Harbora.Tests.Launch;

/// <summary>
/// What a person typed on the confirm screen, across a step that failed before it reached the app.
/// A real protector throughout, because "the row never holds it in the open" is the claim, and the
/// test protector leaves its input readable inside what it returns.
/// </summary>
public sealed class LaunchAnswersTests
{
    private const string StripeKey = "sk_live_abc123";

    private static LaunchHarness Harness() => new() { Protector = new AesGcmSecretProtector("launch-answers-test-key") };

    private static Func<LaunchChoices, LaunchChoices> Typing(string key, string value) => c => c with
    {
        EnvVars = c.EnvVars.Select(v => v.Key == key ? v with { Value = value } : v).ToList()
    };

    [Fact]
    public async Task A_retry_sets_what_was_typed_and_the_row_only_ever_held_it_sealed()
    {
        using var h = Harness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        h.Creates.RefuseApp = new ServiceRefusal("No address left.", "نشانی نمانده است.");

        var failed = await h.ConfirmAsync(id, Typing("STRIPE_SECRET_KEY", StripeKey));

        failed.Status.Should().Be(LaunchStatus.Failed);
        failed.FailureMessage.Should().NotContain("not kept", "it was kept, so the sentence that said otherwise has gone");
        failed.FailureMessage.Should().NotContain(StripeKey);
        failed.FailureMessageFa.Should().NotContain("نگه داشته نشد").And.NotContain(StripeKey);

        var held = h.Row(id).ChoicesJson;
        held.Should().NotContain(StripeKey);
        LaunchJson.ReadAnswers(held)!.Names.Should().Contain("STRIPE_SECRET_KEY");
        LaunchJson.ReadChoices(held)!.EnvVars.Should().OnlyContain(v => v.Value == null, "the choices themselves still carry no value");

        var retried = (await h.Request().RetryAsync(id, null, default)).Value!;

        var variables = h.Store().EnvironmentVariables.IgnoreQueryFilters().ToList();
        var stripe = variables.Single(v => v.Key == "STRIPE_SECRET_KEY");
        stripe.IsSecret.Should().BeTrue();
        stripe.Value.Should().NotContain(StripeKey, "it is stored protected on the app");
        h.Protector.Unprotect(stripe.Value).Should().Be(StripeKey);
        variables.Single(v => v.Key == "LOG_LEVEL").Value.Should().Be("info");
        variables.Should().Contain(v => v.Key == "JWT_SECRET" && v.IsSecret);

        retried.Steps.Single(s => s.Key == LaunchStepKeys.App).Detail.Should().BeNull("nothing is left unset");
        var after = h.Row(id).ChoicesJson;
        LaunchJson.ReadAnswers(after).Should().BeNull("the variables are on the app, so the row lets go of them");
        after.Should().NotContain(LaunchJson.AnswersMarker);
        LaunchJson.ReadChoices(after)!.Name.Should().Be(LaunchJson.ReadChoices(held)!.Name, "only the answers came off");
    }

    [Fact]
    public async Task A_blank_answer_stays_blank_on_the_retry_rather_than_turning_back_into_the_example()
    {
        using var h = Harness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        h.Creates.RefuseApp = new ServiceRefusal("No address left.", "نشانی نمانده است.");

        // LOG_LEVEL is offered as "info". Cleared on the screen, it means "do not set it".
        await h.ConfirmAsync(id, c => c with
        {
            EnvVars = c.EnvVars.Select(v => v.Key == "LOG_LEVEL" ? v with { Value = null } : v).ToList()
        });
        await h.Request().RetryAsync(id, null, default);

        h.Store().EnvironmentVariables.IgnoreQueryFilters().Should().NotContain(v => v.Key == "LOG_LEVEL",
            "the retry is handed exactly what the confirm was, blanks included");
    }

    [Fact]
    public async Task Once_the_variables_are_on_the_app_nothing_typed_stays_on_the_row()
    {
        using var h = Harness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());

        await h.ConfirmAsync(id, Typing("STRIPE_SECRET_KEY", StripeKey));

        h.Row(id).Status.Should().Be(LaunchStatus.WaitingForServices);
        h.Row(id).ChoicesJson.Should().NotContain(LaunchJson.AnswersMarker).And.NotContain(StripeKey);
    }

    [Fact]
    public async Task Answers_that_cannot_be_opened_any_more_are_named_as_unset_and_never_logged()
    {
        using var h = Harness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        h.Creates.RefuseApp = new ServiceRefusal("No address left.", "نشانی نمانده است.");
        await h.ConfirmAsync(id, Typing("STRIPE_SECRET_KEY", StripeKey));

        // The master key changed between the confirm and the retry.
        h.Protector = new AesGcmSecretProtector("a-different-key");
        var retried = (await h.Request().RetryAsync(id, null, default)).Value!;

        h.Store().EnvironmentVariables.IgnoreQueryFilters().Should().NotContain(v => v.Key == "STRIPE_SECRET_KEY");
        retried.Steps.Single(s => s.Key == LaunchStepKeys.App).Detail.Should().Contain("STRIPE_SECRET_KEY");
        h.Logs.Lines.Should().Contain(l => l.Contains("could not be read"));
        h.Logs.Lines.Should().NotContain(l => l.Contains(StripeKey));
    }

    [Fact]
    public async Task A_failed_launch_holding_only_answers_is_swept_after_a_day()
    {
        using var h = Harness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        h.Creates.RefuseApp = new ServiceRefusal("No address left.", "نشانی نمانده است.");
        await h.ConfirmAsync(id, Typing("STRIPE_SECRET_KEY", StripeKey));
        h.Row(id).EncryptedGitToken.Should().BeNull("there was no token: the answers are all it holds");

        h.Advance(TimeSpan.FromHours(23));
        await h.TickAsync();
        LaunchJson.ReadAnswers(h.Row(id).ChoicesJson).Should().NotBeNull("a retry is still open");

        h.Advance(TimeSpan.FromHours(2));
        await h.TickAsync();

        var row = h.Row(id);
        LaunchJson.ReadAnswers(row.ChoicesJson).Should().BeNull();
        LaunchJson.ReadChoices(row.ChoicesJson).Should().NotBeNull("the choices themselves are history and stay");
        row.FailureMessage.Should().Contain("start a new one");
    }

    [Fact]
    public async Task Answers_swept_from_a_launch_that_has_an_app_are_named_so_the_retry_is_not_a_surprise()
    {
        using var h = Harness();
        var id = await h.ReadyGitAsync(NodeWithPostgres());
        h.Creates.RefuseApp = new ServiceRefusal("No address left.", "نشانی نمانده است.");
        await h.ConfirmAsync(id, Typing("STRIPE_SECRET_KEY", StripeKey));

        // Stopped after the app was made and before its variables were, which a retry can still finish.
        h.ChangeRow(id, l => l.AppId = Guid.CreateVersion7());
        h.Advance(TimeSpan.FromHours(25));
        await h.TickAsync();

        var row = h.Row(id);
        LaunchJson.ReadAnswers(row.ChoicesJson).Should().BeNull();
        row.FailureMessage.Should().Contain("STRIPE_SECRET_KEY").And.Contain("not kept past a day").And.NotContain(StripeKey);
        row.FailureMessageFa.Should().Contain("STRIPE_SECRET_KEY").And.NotContain(StripeKey);
    }

    [Fact]
    public void A_row_written_before_answers_were_kept_reads_as_holding_none()
    {
        var old = LaunchJson.Write(new LaunchChoices { Name = "shop" });

        LaunchJson.ReadAnswers(old).Should().BeNull();
        LaunchJson.WithoutAnswers(old).Should().Be(old);
        LaunchJson.ReadAnswers(null).Should().BeNull();
        LaunchJson.ReadAnswers("{not json").Should().BeNull();
    }

    [Fact]
    public void Sealed_answers_print_their_names_and_never_their_ciphertext()
    {
        new SealedAnswers(["STRIPE_SECRET_KEY"], "c2VjcmV0").ToString()
            .Should().Contain("STRIPE_SECRET_KEY").And.NotContain("c2VjcmV0");
    }
}
