using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Encryption on the wire for a managed database.
///
/// It matters because of what external access now does: a grant publishes a port to the internet,
/// and without this the password and every row that follows it cross the network in the clear, where
/// anyone on the path can read them. On the private Docker network that was a defensible trade; the
/// moment the port is public it stops being one.
///
/// The engines differ, and the difference is worth stating rather than papering over:
///
/// <list type="bullet">
/// <item><b>MariaDB and MySQL</b> generate their own certificate at first start and negotiate TLS
/// 1.3 with any client that asks. Nothing to do — and doing something anyway would replace a working
/// certificate with one of ours for no gain.</item>
/// <item><b>PostgreSQL</b> ships with <c>ssl = off</c> and will not turn it on without a certificate
/// file it can read. That is what this class supplies.</item>
/// </list>
///
/// TLS cannot be terminated at the gateway instead. PostgreSQL and MySQL both begin a connection in
/// plaintext and negotiate encryption inside their own protocol, so a proxy that expects TLS from
/// the first byte — the way HTTPS works — is one no client can talk to.
/// </summary>
public static class DatabaseTls
{
    /// <summary>Engines Harbora has to configure. The others already encrypt.</summary>
    public static bool NeedsConfiguring(ManagedServiceType type) => type == ManagedServiceType.PostgreSql;

    /// <summary>Engines that arrive encrypted with no help from us.</summary>
    public static bool EncryptedByDefault(ManagedServiceType type) =>
        type is ManagedServiceType.MySql or ManagedServiceType.MariaDb;

    /// <summary>Whether a connection to this engine can be encrypted at all, once provisioned.</summary>
    public static bool Available(ManagedServiceType type) =>
        NeedsConfiguring(type) || EncryptedByDefault(type);

    /// <summary>Its own volume, so the certificate survives a re-provision and the data volume stays data.</summary>
    public static string VolumeName(string containerName) => $"{containerName}-certs";

    public const string MountPath = "/certs";
    private const string Certificate = MountPath + "/server.crt";
    private const string Key = MountPath + "/server.key";

    /// <summary>
    /// A self-signed certificate and its key, as PEM.
    ///
    /// Made here rather than by shelling out to openssl, because the postgres image does not carry
    /// the openssl binary — the first attempt at this failed on the server with "openssl: not
    /// found" — and the alternatives were to pull an image purely to run one command or to make the
    /// caller depend on one that happened to have it.
    ///
    /// Ten years, because a database certificate that expires is a database that stops accepting
    /// connections on a morning when nobody connects the two events.
    /// </summary>
    public static (string Certificate, string Key) Generate(string commonName, DateTimeOffset now)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={Sanitise(commonName)}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

        // Backdated an hour so a client whose clock runs slightly behind the server does not reject
        // a certificate that was valid the moment it was made.
        using var certificate = request.CreateSelfSigned(now.AddHours(-1), now.AddYears(10));

        return (certificate.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Environment carrying the PEMs — never argv, where `docker inspect` would keep the key.</summary>
    public static IReadOnlyDictionary<string, string> PrepareEnvironment(string certificate, string key) =>
        new Dictionary<string, string>
        {
            ["HARBORA_TLS_CERT"] = certificate,
            ["HARBORA_TLS_KEY"] = key
        };

    /// <summary>
    /// Writes the certificate, once.
    ///
    /// Guarded by a test for the file, because this runs on every provision and rewriting would
    /// change the certificate under clients that had pinned it.
    ///
    /// The ownership and mode are the whole reason this is a container step rather than a line of
    /// config: PostgreSQL refuses to start if the key is readable by anyone but its own user.
    /// </summary>
    public static IReadOnlyList<string> PrepareCommand() =>
    [
        "sh", "-c",
        $"set -e; if [ ! -f {Certificate} ]; then " +
        $"printf '%s' \"$HARBORA_TLS_CERT\" > {Certificate}; " +
        $"printf '%s' \"$HARBORA_TLS_KEY\" > {Key}; fi; " +

        // Applied every time, not only after writing: a volume restored from a backup, or one
        // written by an older version of this code, would otherwise keep permissions that stop the
        // database booting — and the failure reads as data corruption.
        $"chmod 600 {Key}; chmod 644 {Certificate}; chown 999:999 {Key} {Certificate}"
    ];

    /// <summary>Only needs a shell, so the database's own image will do and nothing new is pulled.</summary>
    public const string PrepareImage = "postgres:16-alpine";

    /// <summary>How the server is started once the certificate exists.</summary>
    public static IReadOnlyList<string> ServerCommand() =>
    [
        "postgres",
        "-c", "ssl=on",
        "-c", $"ssl_cert_file={Certificate}",
        "-c", $"ssl_key_file={Key}"
    ];

    /// <summary>
    /// What a client must be told to get an encrypted connection.
    ///
    /// <c>require</c> and not <c>verify-full</c>: the certificate is self-signed, so a client asked
    /// to verify it would refuse to connect. This encrypts the connection and does not prove who is
    /// at the other end — worth being honest about rather than implying more than it does.
    /// </summary>
    public static string? ConnectionParameter(ManagedServiceType type) => type switch
    {
        ManagedServiceType.PostgreSql => "sslmode=require",
        ManagedServiceType.MySql or ManagedServiceType.MariaDb => "ssl-mode=REQUIRED",
        _ => null
    };

    /// <summary>Common name for the certificate — cosmetic, since nothing verifies it.</summary>
    private static string Sanitise(string value)
    {
        var clean = new string((value ?? "harbora").Trim()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.').ToArray());

        return clean.Length == 0 ? "harbora" : clean[..Math.Min(63, clean.Length)];
    }
}
