using System.Security.Cryptography;
using System.Text;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Security;

/// <summary>
/// AES-256-GCM authenticated encryption for secrets at rest. The 32-byte master key comes
/// from configuration (HARBORA_MASTER_KEY) which the installer generates once and stores
/// outside the database. Output: base64( nonce(12) | tag(16) | ciphertext ).
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private readonly byte[] _key;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public AesGcmSecretProtector(string masterKeyBase64)
    {
        _key = DeriveKey(masterKeyBase64);
    }

    private static byte[] DeriveKey(string material)
    {
        // Accept a raw 32-byte base64 key, or derive one from arbitrary text via SHA-256.
        try
        {
            var raw = Convert.FromBase64String(material);
            if (raw.Length == 32) return raw;
        }
        catch (FormatException) { /* fall through to hash */ }
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, cipher, tag);

        var output = new byte[NonceSize + TagSize + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
        Buffer.BlockCopy(cipher, 0, output, NonceSize + TagSize, cipher.Length);
        return Convert.ToBase64String(output);
    }

    public string Unprotect(string ciphertext)
    {
        var input = Convert.FromBase64String(ciphertext);
        var nonce = input.AsSpan(0, NonceSize);
        var tag = input.AsSpan(NonceSize, TagSize);
        var cipher = input.AsSpan(NonceSize + TagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
