using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Nodes;

/// <summary>A certificate the CA just issued, plus the chain to verify it with.</summary>
public sealed record SignedNodeCertificate(
    string CertificatePem,
    string CaCertificatePem,
    string Thumbprint,
    string SerialNumber,
    DateTimeOffset NotAfter);

/// <summary>
/// The certificate authority that node credentials chain to.
///
/// <para>
/// Its own key lives in the settings table, encrypted with the platform master key — the same key
/// that protects every other secret at rest, so a stolen database dump is no more useful here than
/// it is anywhere else, and losing the master key is already understood to be catastrophic.
/// </para>
///
/// <para>
/// The CA is created on first use rather than at install: a Harbora that never enrolls a node never
/// mints one, and an operator who does enroll one does not have to have known to run a setup step
/// months earlier.
/// </para>
/// </summary>
public sealed class NodeCertificateAuthority(
    HarboraDbContext db,
    ISecretProtector protector,
    ILogger<NodeCertificateAuthority> log)
{
    private const string CertificateSettingKey = "nodeagent.ca.certificate";
    private const string KeySettingKey = "nodeagent.ca.key";

    /// <summary>Long, because rotating it means re-enrolling every node in the fleet.</summary>
    private static readonly TimeSpan CaLifetime = TimeSpan.FromDays(3650);

    /// <summary>
    /// Node certificate lifetime. Short enough that a leaked credential expires on a human
    /// timescale, long enough that renewal (which starts at two thirds) is not a daily event.
    /// </summary>
    public static readonly TimeSpan NodeCertificateLifetime = TimeSpan.FromDays(90);

    /// <summary>Client authentication. Deliberately not server auth — see <see cref="SignAsync"/>.</summary>
    private static readonly Oid ClientAuthentication = new("1.3.6.1.5.5.7.3.2");

    /// <summary>Server authentication, for the gateway's own listener.</summary>
    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");

    private static readonly SemaphoreSlim CreationGate = new(1, 1);

    /// <summary>The CA certificate with its private key, creating it on first use.</summary>
    public async Task<X509Certificate2> GetOrCreateAsync(CancellationToken ct)
    {
        if (await TryLoadAsync(ct) is { } existing) return existing;

        // One creation at a time in this process; the unique index on Setting.Key handles the
        // multi-instance race, which the re-read below then resolves.
        await CreationGate.WaitAsync(ct);
        try
        {
            if (await TryLoadAsync(ct) is { } raced) return raced;
            return await CreateAsync(ct);
        }
        finally
        {
            CreationGate.Release();
        }
    }

    /// <summary>The CA certificate without its key — what a node is told to trust.</summary>
    public async Task<string> GetCaCertificatePemAsync(CancellationToken ct)
    {
        using var ca = await GetOrCreateAsync(ct);
        return ca.ExportCertificatePem();
    }

    /// <summary>
    /// Issue a node certificate from a CSR.
    ///
    /// <para>
    /// The CSR contributes exactly two things: a public key, and a signature over itself proving the
    /// requester holds the matching private key. Everything else — subject, validity, key usage,
    /// extended key usage, basic constraints — is set here. Copying a CSR's extensions through would
    /// let a node request <c>CA:true</c> and be handed an authority certificate for the whole fleet;
    /// building the request from the public key alone makes that impossible rather than merely
    /// disallowed.
    /// </para>
    /// </summary>
    public async Task<SignedNodeCertificate> SignAsync(
        string csrPem, string nodeId, string nodeName, CancellationToken ct)
    {
        using var ca = await GetOrCreateAsync(ct);

        CertificateRequest incoming;
        try
        {
            // Loading verifies the CSR's self-signature, which is the proof of possession. Extensions
            // are loaded only so that loading does not fail on a CSR that carries them; they are
            // discarded a line later.
            incoming = CertificateRequest.LoadSigningRequestPem(
                csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw new NodeCertificateException("The certificate signing request could not be read or its signature did not verify.", e);
        }

        var subject = new X500DistinguishedNameBuilder();
        subject.AddOrganizationName("Harbora");
        subject.AddOrganizationalUnitName("node-agent");
        subject.AddCommonName(nodeId);

        // Built from the public key, not from the incoming request. This is the line that makes the
        // paragraph above true.
        var request = new CertificateRequest(subject.Build(), incoming.PublicKey, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, critical: true));

        // Client auth only. A node certificate that could also serve TLS would be a ready-made
        // credential for standing up something that impersonates the control plane to other nodes.
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([ClientAuthentication], critical: true));

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));

        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddUri(new Uri($"harbora://node/{nodeId}"));
        if (!string.IsNullOrWhiteSpace(nodeName)) alternativeNames.AddDnsName(Sanitise(nodeName));
        request.CertificateExtensions.Add(alternativeNames.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = notBefore + NodeCertificateLifetime;

        // Serials must be positive; a leading zero byte keeps the big-endian value out of the
        // negative range without shrinking the entropy that matters.
        var serial = new byte[17];
        RandomNumberGenerator.Fill(serial.AsSpan(1));

        using var issued = request.Create(ca, notBefore, notAfter, serial);

        log.LogInformation(
            "Issued a node certificate for {NodeId} (serial {Serial}), valid until {NotAfter:u}.",
            nodeId, issued.SerialNumber, notAfter);

        return new SignedNodeCertificate(
            issued.ExportCertificatePem(),
            ca.ExportCertificatePem(),
            issued.Thumbprint,
            issued.SerialNumber,
            notAfter);
    }

    /// <summary>
    /// Issue the TLS certificate the TCP gateway serves.
    ///
    /// <para>
    /// Signed by the same CA the nodes already trust, so a gateway on an internal hostname needs no
    /// public certificate and no separate trust decision. Server auth only — the mirror image of the
    /// node certificates, which are client auth only, so neither can be used in the other's role.
    /// </para>
    /// </summary>
    public async Task<X509Certificate2> IssueGatewayCertificateAsync(string host, CancellationToken ct)
    {
        using var ca = await GetOrCreateAsync(ct);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var subject = new X500DistinguishedNameBuilder();
        subject.AddOrganizationName("Harbora");
        subject.AddOrganizationalUnitName("node-gateway");
        subject.AddCommonName(host);

        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([ServerAuthentication], critical: true));

        var alternativeNames = new SubjectAlternativeNameBuilder();
        if (System.Net.IPAddress.TryParse(host, out var address)) alternativeNames.AddIpAddress(address);
        else alternativeNames.AddDnsName(host);
        request.CertificateExtensions.Add(alternativeNames.Build());

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        var serial = new byte[17];
        RandomNumberGenerator.Fill(serial.AsSpan(1));

        using var issued = request.Create(ca, notBefore, notBefore + TimeSpan.FromDays(365), serial);

        // Re-created from PEM so the returned certificate owns a key handle the TLS stack accepts on
        // Windows as well as Linux; the same round-trip the agent does for its own credential.
        return X509Certificate2.CreateFromPem(issued.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>
    /// Whether a presented certificate chains to this CA. Used by the channel and the renewal
    /// endpoint; the node row's thumbprint check is a separate, later question.
    /// </summary>
    public async Task<bool> ValidatesAsync(X509Certificate2 presented, CancellationToken ct)
    {
        using var ca = await GetOrCreateAsync(ct);
        using var chain = new X509Chain();

        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);

        // The CA is ours and issues no CRL, so there is nothing to check and asking would only add a
        // network timeout to every connection. Revocation is enforced on the node row instead, which
        // is authoritative and immediate.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        return chain.Build(presented);
    }

    private async Task<X509Certificate2?> TryLoadAsync(CancellationToken ct)
    {
        // IgnoreQueryFilters throughout: enrollment and the channel are authenticated by a token or
        // a certificate, not by a session, so a filtered read here would come back empty and the
        // panel would helpfully mint a second CA on every request.
        var certificate = await db.Settings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Key == CertificateSettingKey, ct);

        var key = await db.Settings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Key == KeySettingKey, ct);

        if (certificate is null || key is null) return null;

        try
        {
            return X509Certificate2.CreateFromPem(certificate.Value, protector.Unprotect(key.Value));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            // Almost always a master key that changed. Saying so beats a stack trace about padding.
            throw new NodeCertificateException(
                "The node CA is present but could not be decrypted. This usually means HARBORA_MASTER_KEY changed; " +
                "every enrolled node would have to be re-enrolled against a new CA.", e);
        }
    }

    private async Task<X509Certificate2> CreateAsync(CancellationToken ct)
    {
        log.LogWarning("No node CA exists yet; creating one. Every node enrolled from now on chains to it.");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var subject = new X500DistinguishedNameBuilder();
        subject.AddOrganizationName("Harbora");
        subject.AddCommonName("Harbora Node CA");

        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);

        // pathLengthConstraint 0: this CA signs leaf certificates and nothing that can sign further.
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: true, 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var ca = request.CreateSelfSigned(notBefore, notBefore + CaLifetime);

        db.Settings.Add(new Setting { Key = CertificateSettingKey, Value = ca.ExportCertificatePem() });
        db.Settings.Add(new Setting
        {
            Key = KeySettingKey,
            Value = protector.Protect(key.ExportPkcs8PrivateKeyPem()),
            IsSecret = true,
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another instance won the race against the unique index on Setting.Key. Theirs is as
            // good as ours; discard the local one and read what landed.
            foreach (var entry in db.ChangeTracker.Entries<Setting>().ToList()) entry.State = EntityState.Detached;

            return await TryLoadAsync(ct)
                   ?? throw new NodeCertificateException("The node CA could not be created or read back.");
        }

        return X509Certificate2.CreateFromPem(ca.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Keep a node name that is not a DNS label out of a SAN that expects one.</summary>
    private static string Sanitise(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray())
            .Trim('-', '.');

        return cleaned.Length is 0 ? "node" : cleaned[..Math.Min(63, cleaned.Length)];
    }
}

public sealed class NodeCertificateException(string message, Exception? inner = null) : Exception(message, inner);
