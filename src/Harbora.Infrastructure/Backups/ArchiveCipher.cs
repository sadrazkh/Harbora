using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Streaming AES-GCM encryption for backup artifacts at rest (doc 10 §2.5).
///
/// Volume and database archives contain raw application data — the very thing an attacker who
/// reaches the backup destination (an S3 bucket, a mounted disk) is after. They are encrypted here
/// before leaving the staging directory.
///
/// Chunked deliberately: a whole-file AES-GCM would need the entire archive in memory, and database
/// dumps are exactly the artifacts that are too big for that. Each chunk carries its own nonce and
/// authentication tag, so tampering with any chunk fails the read rather than yielding garbage.
///
/// Layout: <c>MAGIC(6) VERSION(1) CHUNKSIZE(4) [ NONCE(12) LEN(4) TAG(16) CIPHERTEXT(LEN) ]*</c>
/// The chunk index is bound into each chunk's associated data, so chunks cannot be reordered,
/// duplicated or dropped without detection.
/// </summary>
public static class ArchiveCipher
{
    private static readonly byte[] Magic = "HRBENC"u8.ToArray();
    private const byte Version = 1;
    private const int NonceSize = 12;   // AES-GCM standard
    private const int TagSize = 16;
    public const int DefaultChunkSize = 1 << 20;   // 1 MiB

    /// <summary>File suffix marking an encrypted artifact.</summary>
    public const string Extension = ".enc";

    public static async Task EncryptAsync(
        Stream plaintext, Stream destination, byte[] key, CancellationToken ct, int chunkSize = DefaultChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        await destination.WriteAsync(Magic, ct);
        await destination.WriteAsync(new[] { Version }, ct);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, chunkSize);
        await destination.WriteAsync(header, ct);

        using var aes = new AesGcm(key, TagSize);
        var buffer = new byte[chunkSize];
        var cipher = new byte[chunkSize];
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var lengthPrefix = new byte[4];
        long index = 0;

        while (true)
        {
            var read = await ReadChunkAsync(plaintext, buffer, ct);
            if (read == 0) break;

            RandomNumberGenerator.Fill(nonce);
            aes.Encrypt(nonce, buffer.AsSpan(0, read), cipher.AsSpan(0, read), tag, AssociatedData(index));

            await destination.WriteAsync(nonce, ct);
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, read);
            await destination.WriteAsync(lengthPrefix, ct);
            await destination.WriteAsync(tag, ct);
            await destination.WriteAsync(cipher.AsMemory(0, read), ct);

            index++;
            if (read < chunkSize) break;   // short read means end of stream
        }
    }

    public static async Task DecryptAsync(
        Stream ciphertext, Stream destination, byte[] key, CancellationToken ct)
    {
        var magic = new byte[Magic.Length];
        await ReadExactlyAsync(ciphertext, magic, ct);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidOperationException("Not a Harbora encrypted archive.");

        var version = new byte[1];
        await ReadExactlyAsync(ciphertext, version, ct);
        if (version[0] != Version)
            throw new InvalidOperationException($"Unsupported archive encryption version {version[0]}.");

        var header = new byte[4];
        await ReadExactlyAsync(ciphertext, header, ct);
        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (chunkSize is < 1 or > (1 << 26))
            throw new InvalidOperationException("Encrypted archive declares an implausible chunk size.");

        using var aes = new AesGcm(key, TagSize);
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var lengthPrefix = new byte[4];
        var cipher = new byte[chunkSize];
        var plain = new byte[chunkSize];
        long index = 0;

        while (true)
        {
            var got = await ReadChunkAsync(ciphertext, nonce, ct);
            if (got == 0) break;                       // clean end of archive
            if (got != NonceSize) throw new CryptographicException("Encrypted archive is truncated.");

            await ReadExactlyAsync(ciphertext, lengthPrefix, ct);
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
            if (length < 1 || length > chunkSize)
                throw new CryptographicException("Encrypted archive declares an invalid chunk length.");

            await ReadExactlyAsync(ciphertext, tag, ct);
            await ReadExactlyAsync(ciphertext, cipher.AsMemory(0, length), ct);

            // Throws CryptographicException if the chunk was altered, reordered or truncated.
            aes.Decrypt(nonce, cipher.AsSpan(0, length), tag, plain.AsSpan(0, length), AssociatedData(index));

            await destination.WriteAsync(plain.AsMemory(0, length), ct);
            index++;
        }
    }

    /// <summary>Cheap check that a file looks like one of our encrypted archives.</summary>
    public static async Task<bool> IsEncryptedArchiveAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var magic = new byte[Magic.Length];
        var read = await ReadChunkAsync(file, magic, ct);
        return read == Magic.Length && magic.AsSpan().SequenceEqual(Magic);
    }

    /// <summary>Binds the chunk's position into its tag so chunks can't be shuffled or dropped.</summary>
    private static byte[] AssociatedData(long index)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(data, index);
        return data;
    }

    private static async Task<int> ReadChunkAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        if (await ReadChunkAsync(stream, buffer, ct) != buffer.Length)
            throw new CryptographicException("Encrypted archive is truncated.");
    }
}
