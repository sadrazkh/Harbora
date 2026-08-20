using System.Security.Claims;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Security;
using Harbora.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The two questions that decide whether an account can still be entered after an unlink, asked
/// away from the controller because both are asked in two places — the settings page, which decides
/// whether to draw a Disconnect button, and the POST, which decides whether to honour it. Two copies
/// of this rule is how a page offers a button that refuses, or worse, one that does not.
/// </summary>
public class ExternalLoginPolicyTests
{
    [Fact]
    public void An_account_provisioned_by_a_provider_has_no_password_the_sign_in_form_would_accept()
    {
        // What ProvisionFromExternalAsync stores, and what the column's non-nullable default is.
        ExternalLoginPolicy.HasUsablePassword("").Should().BeFalse();
        ExternalLoginPolicy.HasUsablePassword(null).Should().BeFalse();
        ExternalLoginPolicy.HasUsablePassword("   ").Should().BeFalse();
    }

    [Fact]
    public void A_real_hash_is_read_as_a_password_and_so_is_the_hasher_that_wrote_it()
    {
        var hash = new Pbkdf2PasswordHasher().Hash("correct-horse-battery-staple");

        ExternalLoginPolicy.HasUsablePassword(hash).Should().BeTrue();
        // The agreement that makes the policy meaningful: what it calls usable is what actually opens
        // the door, and what it calls unusable is refused rather than throwing at the login form.
        new Pbkdf2PasswordHasher().Verify("correct-horse-battery-staple", hash).Should().BeTrue();
        new Pbkdf2PasswordHasher().Verify("anything", "").Should().BeFalse();
    }

    [Theory]
    // no password, no other provider — the refusal exists for exactly this row
    [InlineData(false, 0, true)]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 3, false)]
    public void Unlinking_only_refuses_when_nothing_at_all_would_be_left(
        bool hasPassword, int othersRemaining, bool refuses) =>
        ExternalLoginPolicy.WouldLeaveNoWayIn(hasPassword, othersRemaining).Should().Be(refuses);

    /// <summary>
    /// A count that somehow came back negative must read as "nothing left", not as "fine" — the
    /// refusal is the safe direction and an off-by-one here locks somebody out permanently.
    /// </summary>
    [Fact]
    public void A_negative_count_is_read_as_nothing_left_rather_than_as_a_provider()
    {
        ExternalLoginPolicy.WouldLeaveNoWayIn(hasUsablePassword: false, otherLinksRemaining: -1)
            .Should().BeTrue();
    }
}

/// <summary>
/// The uniqueness the whole feature rests on, asserted against the model rather than against a
/// database.
///
/// <para>
/// It has to be asserted here because the HTTP harness runs on EF InMemory, which does not enforce a
/// unique index at all — so a controller guard that happened to be removed would leave every
/// behavioural test green and only fail in production. The index itself is exercised for real in the
/// Postgres lane and by the migration.
/// </para>
/// </summary>
public class ExternalLoginUniquenessTests
{
    private static Microsoft.EntityFrameworkCore.Metadata.IEntityType Entity()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Harbora.Data.HarboraDbContext>()
            .UseInMemoryDatabase("external-login-model").Options;
        using var db = new Harbora.Data.HarboraDbContext(options);
        return db.Model.FindEntityType(typeof(ExternalLogin))!;
    }

    [Fact]
    public void One_identity_at_one_provider_is_one_row()
    {
        Entity().GetIndexes()
            .Where(i => i.IsUnique)
            .Should().Contain(i => i.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(ExternalLogin.Provider), nameof(ExternalLogin.Subject) }));
    }

    [Fact]
    public void And_one_account_holds_at_most_one_of_each_provider()
    {
        Entity().GetIndexes()
            .Where(i => i.IsUnique)
            .Should().Contain(i => i.Properties.Select(p => p.Name).SequenceEqual(
                new[] { nameof(ExternalLogin.UserId), nameof(ExternalLogin.Provider) }),
                "otherwise the settings page could not say which row a Disconnect button means");
    }

    [Fact]
    public void Removing_an_account_takes_its_external_logins_with_it()
    {
        Entity().GetForeignKeys().Should().ContainSingle()
            .Which.DeleteBehavior.Should().Be(Microsoft.EntityFrameworkCore.DeleteBehavior.Cascade);
    }
}

/// <summary>
/// Reducing a provider's principal to the five things the linking rules read. The providers spell
/// the same facts differently — Google and GitHub answer <c>nameidentifier</c>, an OIDC id_token
/// answers <c>sub</c> — and a subject read as null is an identity that cannot be stored at all.
/// </summary>
public class ExternalIdentityReadingTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void An_identity_with_no_subject_cannot_be_read_because_the_subject_is_the_key()
    {
        ExternalAuth.Read(ExternalLoginProviders.Google,
            Principal((ClaimTypes.Email, "someone@example.com"))).Should().BeNull();
    }

    [Fact]
    public void An_oidc_provider_naming_the_subject_sub_is_read_the_same_as_the_others()
    {
        var identity = ExternalAuth.Read(ExternalLoginProviders.Oidc,
            Principal(("sub", "abc-123"), ("email", "Person@Example.COM"), ("name", "A Person")));

        identity.Should().NotBeNull();
        identity!.Subject.Should().Be("abc-123");
        identity.Email.Should().Be("person@example.com", "an address is matched lower-cased, like every other one here");
        identity.DisplayName.Should().Be("A Person");
    }

    [Fact]
    public void Verification_is_only_true_when_the_provider_actually_said_so()
    {
        ExternalAuth.Read(ExternalLoginProviders.Google, Principal(
            (ClaimTypes.NameIdentifier, "1"), (ExternalAuth.EmailVerifiedClaim, "true")))!
            .EmailVerified.Should().BeTrue();

        ExternalAuth.Read(ExternalLoginProviders.Google, Principal(
            (ClaimTypes.NameIdentifier, "1"), (ExternalAuth.EmailVerifiedClaim, "false")))!
            .EmailVerified.Should().BeFalse();

        // Silence is not consent: a provider that says nothing about the address has verified nothing,
        // and an account created on that basis would be trusted for a mailbox nobody proved.
        ExternalAuth.Read(ExternalLoginProviders.GitHub, Principal((ClaimTypes.NameIdentifier, "1")))!
            .EmailVerified.Should().BeFalse();
    }

    [Fact]
    public void A_github_account_with_no_public_address_is_read_as_having_none()
    {
        var identity = ExternalAuth.Read(ExternalLoginProviders.GitHub,
            Principal((ClaimTypes.NameIdentifier, "9"), ("login", "octocat")));

        identity!.Email.Should().BeNull("and the callback refuses rather than inventing one");
        identity.DisplayName.Should().Be("octocat", "the login name is the only name GitHub always has");
    }
}

/// <summary>
/// What the sign-in page is willing to offer. "Switched on" and "usable" are different facts, and a
/// button rendered for the second-but-not-the-first sends somebody to an error on another site.
/// </summary>
public class ExternalProviderConfigTests
{
    private static ExternalProviderConfig Config(
        string provider, bool enabled = true, string? id = "id", string? secret = "secret",
        string? authority = null) =>
        new(provider, enabled, id, secret, authority, null);

    [Fact]
    public void A_provider_switched_off_offers_nothing_however_completely_it_is_filled_in() =>
        Config(ExternalLoginProviders.Google, enabled: false).IsConfigured.Should().BeFalse();

    [Theory]
    [InlineData(null, "secret")]
    [InlineData("id", null)]
    [InlineData("", "secret")]
    public void A_provider_missing_half_its_credentials_offers_nothing(string? id, string? secret) =>
        Config(ExternalLoginProviders.Google, id: id, secret: secret).IsConfigured.Should().BeFalse();

    [Fact]
    public void The_generic_provider_also_needs_somewhere_to_send_people() =>
        Config(ExternalLoginProviders.Oidc, authority: null).IsConfigured.Should().BeFalse();

    [Fact]
    public void The_generic_provider_is_complete_once_it_has_an_authority() =>
        Config(ExternalLoginProviders.Oidc, authority: "https://sso.example.com")
            .IsConfigured.Should().BeTrue();

    [Fact]
    public void Google_and_github_need_no_authority_because_theirs_is_not_a_setting() =>
        Config(ExternalLoginProviders.GitHub).IsConfigured.Should().BeTrue();

    [Fact]
    public void An_unknown_provider_name_is_not_normalised_into_one_of_ours()
    {
        ExternalLoginProviders.Normalise("facebook").Should().BeNull();
        ExternalLoginProviders.Normalise(null).Should().BeNull();
        ExternalLoginProviders.Normalise("  GOOGLE ").Should().Be(ExternalLoginProviders.Google);
    }

    [Fact]
    public void The_generic_provider_is_called_whatever_the_operator_called_it()
    {
        ExternalLoginProviders.DisplayName(ExternalLoginProviders.Oidc, "Acme ID", isFa: false)
            .Should().Be("Acme ID");
        ExternalLoginProviders.DisplayName(ExternalLoginProviders.Oidc, null, isFa: false)
            .Should().Be("Single sign-on", "a button with a blank name is not a button");
        ExternalLoginProviders.DisplayName(ExternalLoginProviders.Google, "Acme ID", isFa: true)
            .Should().Be("Google", "a proper noun stays itself in both languages");
    }
}
