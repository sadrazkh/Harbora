using Harbora.Domain.Common;

namespace Harbora.Domain.Registries;

/// <summary>
/// A workspace's own username/secret for pulling images off a private container registry — their
/// company's Harbor, a private Docker Hub repository, GHCR, or any other registry that demands
/// authentication before it will serve a manifest (1.3, 2026-09 market-gaps round two).
///
/// <para>
/// Matched to an image by <see cref="RegistryHost"/> alone, never by app or by name — the same
/// registry can serve several apps' images, and a credential is a fact about the registry, not about
/// any one app. <see cref="RegistryHost"/> is normalized (trimmed, lower-cased) before it is stored,
/// and a unique index on (<see cref="WorkspaceId"/>, <see cref="RegistryHost"/>) is what makes
/// matching deterministic: a workspace may have at most one credential per registry host, so there is
/// never a "which one wins" question to answer at pull time. Adding a second credential for a host
/// that already has one is refused at the form, not resolved by picking one silently.
/// </para>
///
/// <para>
/// Mirrors <see cref="Harbora.Domain.Storage.StorageBucket"/> and
/// <see cref="Harbora.Domain.Email.EmailProvider"/>: the secret is encrypted with the platform key
/// through <c>ISecretProtector</c>, never stored or logged in the clear, and never sent back to the
/// panel once saved — only ever re-encrypted with a new value the caller typed.
/// </para>
/// </summary>
public class RegistryCredential : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// The registry's host — <c>ghcr.io</c>, <c>docker.io</c> for a private Docker Hub repository, or
    /// <c>registry.example.com:5000</c> for a self-hosted one with a non-default port. Stored
    /// normalized (trimmed, lower-cased) so a lookup by an image's own parsed host — which is
    /// normalized the same way by <c>ImageDigestResolver.Parse</c> — always matches case-insensitively
    /// without either side having to remember to compare that way.
    /// </summary>
    public string RegistryHost { get; set; } = string.Empty;

    /// <summary>Not a secret on its own — a registry username is usually an account name or a token's
    /// public half (e.g. a GHCR personal-access-token's owner).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>The password or token, encrypted with the platform key like every other stored
    /// credential (<see cref="Harbora.Application.Abstractions.ISecretProtector"/>). Rotation
    /// overwrites this in place — <see cref="BaseEntity.UpdatedAt"/> is the only trace a previous
    /// value ever existed, and nothing else in the platform keeps a copy of it: the next deployment
    /// that pulls from this registry reads this row fresh, so a rotated secret is what a fresh pull
    /// uses from the moment it is saved, and the old one is gone rather than merely superseded.</summary>
    public string EncryptedSecret { get; set; } = string.Empty;
}
