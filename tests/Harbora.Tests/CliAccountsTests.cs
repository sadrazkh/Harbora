using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Signing in to more than one Harbora at a time.
///
/// The config held one server and one token, so logging into a second panel silently replaced the
/// first — and `harbora deploy` had no way to say which account it meant. Existing installs must keep
/// working: their file has the old shape and no profile list.
/// </summary>
public class CliAccountsTests
{
    private static HarboraConfig Legacy(string server, string token) =>
        HarboraConfig.Migrate(new HarboraConfig { Server = server, Token = token });

    [Fact]
    public void An_existing_single_account_config_keeps_working()
    {
        var cfg = Legacy("https://panel.example.com", "tok-1");

        cfg.Resolve().Should().NotBeNull();
        cfg.Resolve()!.Token.Should().Be("tok-1");
        cfg.NeedsAccountChoice.Should().BeFalse("there is still only one account");
    }

    [Fact]
    public void A_second_login_does_not_replace_the_first()
    {
        var cfg = Legacy("https://work.example.com", "work-token");

        cfg.Upsert("me@home", "https://home.example.com", "home-token");

        cfg.Profiles.Should().HaveCount(2);
        cfg.NeedsAccountChoice.Should().BeTrue();
    }

    [Fact]
    public void Signing_in_again_to_the_same_account_refreshes_the_token()
    {
        // Otherwise every re-login grows the list and the CLI starts asking a question with two
        // identical answers.
        var cfg = new HarboraConfig();
        cfg.Upsert("me@example.com", "https://panel.example.com", "old");

        cfg.Upsert("me@example.com", "https://panel.example.com/", "new");

        cfg.Profiles.Should().ContainSingle().Which.Token.Should().Be("new");
    }

    [Fact]
    public void The_newest_login_becomes_the_default()
    {
        var cfg = new HarboraConfig();
        cfg.Upsert("first@example.com", "https://a.example.com", "a");
        cfg.Upsert("second@example.com", "https://b.example.com", "b");

        cfg.Resolve()!.Name.Should().Be("second@example.com");
    }

    [Fact]
    public void An_account_can_be_named_on_the_command_line()
    {
        var cfg = new HarboraConfig();
        cfg.Upsert("first@example.com", "https://a.example.com", "a");
        cfg.Upsert("second@example.com", "https://b.example.com", "b");

        cfg.Resolve("first@example.com")!.Token.Should().Be("a");
    }

    [Fact]
    public void Naming_an_account_that_is_not_signed_in_resolves_to_nothing()
    {
        // Better to say "not logged in" than to quietly deploy with the wrong account's token.
        var cfg = new HarboraConfig();
        cfg.Upsert("me@example.com", "https://a.example.com", "a");

        cfg.Resolve("someone-else@example.com").Should().BeNull();
    }

    [Fact]
    public void Removing_the_current_account_promotes_another_one()
    {
        var cfg = new HarboraConfig();
        cfg.Upsert("first@example.com", "https://a.example.com", "a");
        var second = cfg.Upsert("second@example.com", "https://b.example.com", "b");

        cfg.Remove(second);

        // Asserting on Current, not just on Resolve(): Resolve falls back to the first profile when
        // Current names nothing, so a stale pointer written to disk would look fine from here while
        // every other reader of the file sees an account that is not signed in.
        cfg.Current.Should().NotBeNull();
        cfg.Profiles.Should().Contain(p => HarboraConfig.Key(p) == cfg.Current);
        cfg.Resolve()!.Name.Should().Be("first@example.com");
    }

    [Fact]
    public void Removing_the_last_account_leaves_nothing_selected()
    {
        var cfg = new HarboraConfig();
        var only = cfg.Upsert("me@example.com", "https://a.example.com", "a");

        cfg.Remove(only);

        cfg.Current.Should().BeNull();
        cfg.HasAny.Should().BeFalse();
    }

    [Fact]
    public void A_trailing_slash_does_not_create_a_second_account()
    {
        var cfg = new HarboraConfig();
        cfg.Upsert("me@example.com", "https://panel.example.com/", "a");
        cfg.Upsert("me@example.com", "https://panel.example.com", "b");

        cfg.Profiles.Should().ContainSingle();
    }

    [Fact]
    public void Signing_in_after_an_upgrade_does_not_create_a_second_account()
    {
        // A migrated config is named after the server, because that is all the old file knew. The
        // first `harbora login` afterwards learns the email — and must claim that profile rather than
        // file a duplicate, or every command starts asking which of two identical accounts to use.
        var cfg = Legacy("https://panel.example.com", "old-token");

        cfg.Upsert("me@example.com", "https://panel.example.com", "fresh-token");

        cfg.Profiles.Should().ContainSingle();
        cfg.Profiles[0].Name.Should().Be("me@example.com");
        cfg.Profiles[0].Token.Should().Be("fresh-token");
        cfg.NeedsAccountChoice.Should().BeFalse();
    }

    [Fact]
    public void A_different_server_is_still_a_separate_account()
    {
        var cfg = Legacy("https://work.example.com", "work-token");

        cfg.Upsert("me@example.com", "https://home.example.com", "home-token");

        cfg.Profiles.Should().HaveCount(2, "adopting a placeholder must not swallow another server");
    }
}
