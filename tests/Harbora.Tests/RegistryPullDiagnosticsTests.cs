using FluentAssertions;
using Harbora.Application.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 1.3 (2026-09 market-gaps round two): turning a registry's raw error text into one of the three
/// distinguishable outcomes a customer can act on, instead of the "image not found" every failed pull
/// reported before this — which sent a customer looking for a typo in a perfectly correct image name
/// when the real cause was a missing or wrong credential.
///
/// <para>
/// The fourth outcome, <see cref="RegistryPullFailureKind.Indeterminate"/>, is not a bug in the
/// classifier — several registries deliberately answer in a way that hides whether a private
/// repository exists at all from an unauthenticated caller (Docker Hub's own daemon message is the
/// canonical example, asserted on below). Saying "cannot tell" is the honest answer in that case;
/// guessing would be inventing a fact the registry never gave up.
/// </para>
/// </summary>
public class RegistryPullDiagnosticsTests
{
    [Fact]
    public void Auth_failure_with_credentials_supplied_is_named_as_rejected_not_missing()
    {
        var ex = RegistryPullDiagnostics.Classify("ghcr.io", credentialSupplied: true,
            "unauthorized: authentication required");

        ex.Kind.Should().Be(RegistryPullFailureKind.CredentialsRejected);
        ex.Message.Should().Contain("ghcr.io");
        ex.Message.Should().Contain("rejected the credentials");
    }

    [Fact]
    public void Auth_failure_with_no_credentials_configured_is_named_as_missing_not_rejected()
    {
        var ex = RegistryPullDiagnostics.Classify("registry.example.com", credentialSupplied: false,
            "401 Unauthorized");

        ex.Kind.Should().Be(RegistryPullFailureKind.CredentialsMissing);
        ex.Message.Should().Contain("registry.example.com");
        ex.Message.Should().Contain("no credentials are configured");
    }

    [Fact]
    public void A_clean_not_found_answer_is_named_as_image_not_found()
    {
        var ex = RegistryPullDiagnostics.Classify("quay.io", credentialSupplied: true,
            "manifest unknown: manifest tagged by \"9.9.9\" is not found");

        ex.Kind.Should().Be(RegistryPullFailureKind.ImageNotFound);
        ex.Message.Should().Contain("quay.io");
        ex.Message.Should().Contain("does not exist");
    }

    [Fact]
    public void Dockers_own_blended_message_is_indeterminate_not_guessed_as_either()
    {
        // The literal message the Docker daemon returns for a private-repo pull with no or wrong
        // credentials — deliberately blended so an unauthenticated caller cannot use the difference
        // to discover a private repository exists.
        var ex = RegistryPullDiagnostics.Classify("docker.io", credentialSupplied: false,
            "pull access denied for acme/private, repository does not exist or may require 'docker login'");

        ex.Kind.Should().Be(RegistryPullFailureKind.Indeterminate);
        ex.Message.Should().Contain("does not distinguish");
    }

    [Fact]
    public void The_same_blended_message_stays_indeterminate_even_with_credentials_supplied()
    {
        // Configuring a credential does not make Docker Hub's own ambiguity resolvable — the message
        // is the same whether the credential was wrong or the image never existed.
        var ex = RegistryPullDiagnostics.Classify("docker.io", credentialSupplied: true,
            "pull access denied for acme/private, repository does not exist or may require 'docker login'");

        ex.Kind.Should().Be(RegistryPullFailureKind.Indeterminate);
    }

    [Fact]
    public void No_detail_at_all_is_indeterminate_rather_than_a_guessed_specific_reason()
    {
        var ex = RegistryPullDiagnostics.Classify("mycompany.harbor.internal", credentialSupplied: false, rawMessage: null);

        ex.Kind.Should().Be(RegistryPullFailureKind.Indeterminate);
        ex.Message.Should().Contain("mycompany.harbor.internal");
    }

    [Fact]
    public void An_unrecognised_message_is_indeterminate_and_quotes_the_registrys_own_words()
    {
        var ex = RegistryPullDiagnostics.Classify("ghcr.io", credentialSupplied: true,
            "502 Bad Gateway from upstream");

        ex.Kind.Should().Be(RegistryPullFailureKind.Indeterminate);
        ex.Message.Should().Contain("502 Bad Gateway from upstream");
    }

    [Fact]
    public void Every_message_names_the_registry_host()
    {
        // The single most valuable part of the task: a pull failure must say WHICH registry refused,
        // not just that something failed.
        foreach (var (host, hasCreds, raw) in new (string, bool, string)[]
        {
            ("ghcr.io", true, "unauthorized"),
            ("registry.example.com:5000", false, "401"),
            ("quay.io", true, "manifest unknown"),
            ("docker.io", false, "")
        })
        {
            RegistryPullDiagnostics.Classify(host, hasCreds, raw).Message.Should().Contain(host);
        }
    }
}
