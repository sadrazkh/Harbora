using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Harbora.NodeAgent.State;

namespace Harbora.NodeAgent.Identity;

/// <summary>The node's credential: a private key that never leaves the box and the certificate over it.</summary>
public sealed record NodeIdentity(X509Certificate2 Certificate, X509Certificate2 CaCertificate)
{
    public DateTimeOffset NotAfter => Certificate.NotAfter.ToUniversalTime();
    public DateTimeOffset NotBefore => Certificate.NotBefore.ToUniversalTime();

    /// <summary>Fraction of the certificate's lifetime already spent, clamped to [0, 1].</summary>
    public double LifetimeElapsed(DateTimeOffset now)
    {
        var total = NotAfter - NotBefore;
        if (total <= TimeSpan.Zero) return 1;
        var elapsed = now - NotBefore;
        return Math.Clamp(elapsed / total, 0, 1);
    }
}

/// <summary>
/// Reads and writes the node's key material.
///
/// <para>
/// The private key is generated here and is never transmitted: enrollment sends a CSR, so the
/// control plane holds only the public half. A stolen panel database therefore cannot impersonate
/// a node — it can revoke one, which is a very different failure.
/// </para>
/// </summary>
public sealed class NodeIdentityStore(string directory)
{
    private const string KeyFile = "node.key.pem";
    private const string CertFile = "node.crt.pem";
    private const string CaFile = "control-plane-ca.crt.pem";

    private readonly Lock _gate = new();

    public string KeyPath => Path.Combine(directory, KeyFile);
    public string CertificatePath => Path.Combine(directory, CertFile);
    public string CaPath => Path.Combine(directory, CaFile);

    public bool HasIdentity => File.Exists(KeyPath) && File.Exists(CertificatePath) && File.Exists(CaPath);

    /// <summary>
    /// Generate a fresh key (if there is none) and produce a CSR for it. Reusing the existing key
    /// during a renewal is deliberate: rotating the certificate is routine and rotating the key on
    /// every renewal would multiply the number of moments a half-written key file can exist.
    /// </summary>
    public string CreateSigningRequest(string commonName, bool newKey)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            FilePermissions.RestrictDirectory(directory);

            using var key = newKey || !File.Exists(KeyPath) ? GenerateAndStoreKey() : LoadKey();

            var request = new CertificateRequest(SubjectName(commonName), key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement,
                    critical: true));

            // Client auth only. A node certificate that could also serve TLS would be a credential
            // for standing something up that impersonates the control plane.
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], critical: true));

            return request.CreateSigningRequestPem();
        }
    }

    /// <summary>Persist the certificate the control plane signed, alongside the CA that signed it.</summary>
    public void StoreCertificate(string certificatePem, string caCertificatePem)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(directory);
            FilePermissions.RestrictDirectory(directory);

            WriteAtomic(CertificatePath, certificatePem);
            WriteAtomic(CaPath, caCertificatePem);
        }
    }

    /// <summary>The identity, or null when the node has not enrolled yet.</summary>
    public NodeIdentity? Load()
    {
        lock (_gate)
        {
            if (!HasIdentity) return null;

            var certificate = LoadCertificateWithKey();
            var ca = X509CertificateLoader.LoadCertificate(
                PemToDer(File.ReadAllText(CaPath), "CERTIFICATE"));

            return new NodeIdentity(certificate, ca);
        }
    }

    /// <summary>Remove every trace of the credential. Used by the uninstaller and by re-enrollment.</summary>
    public void Erase()
    {
        lock (_gate)
        {
            foreach (var path in new[] { KeyPath, CertificatePath, CaPath })
                if (File.Exists(path))
                {
                    ShredKeyMaterial(path);
                    File.Delete(path);
                }
        }
    }

    /// <summary>True when the key file cannot be read by anyone but the agent's user.</summary>
    public bool KeyIsProtected() => FilePermissions.IsOwnerOnly(KeyPath);

    private ECDsa GenerateAndStoreKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        WriteAtomic(KeyPath, key.ExportPkcs8PrivateKeyPem());
        return key;
    }

    private ECDsa LoadKey()
    {
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(KeyPath));
        return key;
    }

    /// <summary>
    /// Combine certificate and key into something the TLS stack will present.
    /// <para>
    /// The PKCS#12 round-trip looks redundant and is not: a certificate built by
    /// <c>CreateFromPem</c> carries an ephemeral key that Schannel refuses to use for client
    /// authentication, so a developer on Windows would see handshake failures a Linux node never
    /// hits. Exporting and reloading gives a key handle both platforms accept.
    /// </para>
    /// </summary>
    private X509Certificate2 LoadCertificateWithKey()
    {
        using var fromPem = X509Certificate2.CreateFromPemFile(CertificatePath, KeyPath);
        return X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pkcs12), password: null);
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        FilePermissions.RestrictFile(temp);
        File.Move(temp, path, overwrite: true);
        FilePermissions.RestrictFile(path);
    }

    /// <summary>
    /// Overwrite before unlinking. On a copy-on-write filesystem this guarantees nothing, which is
    /// why it is a defence in depth rather than the defence — but on the ext4 root most nodes run,
    /// it is the difference between a deleted key and a recoverable one.
    /// </summary>
    private static void ShredKeyMaterial(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            if (length <= 0) return;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            var noise = new byte[length];
            RandomNumberGenerator.Fill(noise);
            stream.Write(noise);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
            // Deleting the file is the part that matters; the overwrite is best effort.
        }
    }

    private static byte[] PemToDer(string pem, string label)
    {
        var fields = PemEncoding.Find(pem);
        var found = pem.AsSpan()[fields.Label];
        if (!found.SequenceEqual(label))
            throw new CryptographicException($"Expected a {label} PEM block, found {found}.");

        return Convert.FromBase64String(pem[fields.Base64Data].Replace("\r", "").Replace("\n", ""));
    }

    /// <summary>
    /// Build the subject structurally rather than by formatting a string.
    ///
    /// <para>
    /// A node name is data. Concatenating it into <c>CN=…,O=Harbora</c> lets a name containing a
    /// comma add its own RDNs — data being read as structure, which is the same shape of bug as SQL
    /// injection. The builder encodes each component separately, so a hostile name is a strange
    /// common name and nothing more.
    /// </para>
    /// </summary>
    private static X500DistinguishedName SubjectName(string commonName)
    {
        var builder = new X500DistinguishedNameBuilder();
        builder.AddOrganizationName("Harbora");
        builder.AddOrganizationalUnitName("node-agent");
        builder.AddCommonName(commonName);
        return builder.Build();
    }
}
