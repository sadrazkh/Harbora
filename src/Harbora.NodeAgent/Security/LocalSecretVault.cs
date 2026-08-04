using System.Security.Cryptography;
using System.Text;
using Harbora.NodeAgent.Identity;

namespace Harbora.NodeAgent.Security;

/// <summary>
/// Encrypts the secrets the node has to keep — database grant passwords, mostly — so they are not
/// plaintext in a state file.
///
/// <para>
/// The key is derived from the node's own private key, which already has to be present for the
/// agent to work at all and is already 0600. That means no second key to distribute, and it means
/// erasing the identity erases every stored secret with it: a re-enrolled node cannot read what the
/// previous one held, which is the correct outcome rather than an inconvenience.
/// </para>
/// </summary>
public sealed class LocalSecretVault(NodeIdentityStore identities)
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>Domain separator, so a key derived here can never coincide with one derived elsewhere.</summary>
    private static readonly byte[] Info = "harbora-node-agent/v1/local-secret-vault"u8.ToArray();

    private static readonly byte[] Salt = "harbora-node-agent"u8.ToArray();

    public sealed class VaultUnavailableException(string message) : Exception(message);

    /// <summary>Encrypt to a self-describing base64 blob: <c>nonce | ciphertext | tag</c>.</summary>
    public string Protect(string plaintext)
    {
        var key = DeriveKey();

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(key, TagBytes))
            aes.Encrypt(nonce, plain, cipher, tag);

        CryptographicOperations.ZeroMemory(key);

        var blob = new byte[NonceBytes + cipher.Length + TagBytes];
        nonce.CopyTo(blob, 0);
        cipher.CopyTo(blob, NonceBytes);
        tag.CopyTo(blob, NonceBytes + cipher.Length);

        return Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypt. Throws on tampering rather than returning something plausible — an authenticated
    /// cipher's whole value is that a modified blob is an error instead of different plaintext.
    /// </summary>
    public string Unprotect(string protectedValue)
    {
        var blob = Convert.FromBase64String(protectedValue);

        if (blob.Length < NonceBytes + TagBytes)
            throw new CryptographicException("The protected value is too short to be a valid vault blob.");

        var key = DeriveKey();

        var nonce = blob.AsSpan(0, NonceBytes);
        var cipher = blob.AsSpan(NonceBytes, blob.Length - NonceBytes - TagBytes);
        var tag = blob.AsSpan(blob.Length - TagBytes);
        var plain = new byte[cipher.Length];

        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Whether the vault can work — i.e. whether the node has an identity yet.</summary>
    public bool IsAvailable => File.Exists(identities.KeyPath);

    private byte[] DeriveKey()
    {
        if (!IsAvailable)
            throw new VaultUnavailableException(
                "The node has no private key yet, so there is nothing to derive a vault key from. Enroll the node first.");

        // The PEM text is hashed rather than used directly: HKDF wants uniform input material, and
        // the whole file (including its header) is a stable, node-specific secret.
        var keyMaterial = SHA256.HashData(File.ReadAllBytes(identities.KeyPath));

        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, keyMaterial, outputLength: 32, Salt, Info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial);
        }
    }

    /// <summary>
    /// A password for a database grant: 192 bits of entropy in an alphabet every engine's client
    /// accepts unquoted. Deliberately excludes characters that would need escaping in a connection
    /// string — a credential nobody can paste is a credential that gets replaced with a weaker one.
    /// </summary>
    public static string GeneratePassword(int length = 32)
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }

    /// <summary>A username that is a valid identifier on every supported engine.</summary>
    public static string GenerateUsername(string prefix = "harbora")
    {
        var suffix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        return $"{prefix}_{suffix}";
    }
}
