using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Harbora.Infrastructure.Backups;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Backup archives hold raw application data, so they are encrypted before leaving the staging
/// directory (doc 10 §2.5). These tests hold the cipher to the two properties that matter: what
/// goes in comes back out exactly, and anything tampered with fails loudly rather than decoding to
/// silent garbage that a restore would then write over live data.
/// </summary>
public class ArchiveCipherTests
{
    private static byte[] Key(byte seed = 1) => Enumerable.Repeat(seed, 32).ToArray();

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, byte[] key, int chunkSize = ArchiveCipher.DefaultChunkSize)
    {
        using var input = new MemoryStream(plaintext);
        using var output = new MemoryStream();
        await ArchiveCipher.EncryptAsync(input, output, key, default, chunkSize);
        return output.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] ciphertext, byte[] key)
    {
        using var input = new MemoryStream(ciphertext);
        using var output = new MemoryStream();
        await ArchiveCipher.DecryptAsync(input, output, key, default);
        return output.ToArray();
    }

    [Fact]
    public async Task Round_trips_content_exactly()
    {
        var plaintext = Encoding.UTF8.GetBytes("harbora backup payload — with non-ascii ✓");

        var restored = await DecryptAsync(await EncryptAsync(plaintext, Key()), Key());

        restored.Should().Equal(plaintext);
    }

    [Fact]
    public async Task Round_trips_data_larger_than_one_chunk()
    {
        // Database dumps are exactly the artifacts too big to hold in memory, so the chunked path
        // is the one that matters in production.
        var plaintext = RandomNumberGenerator.GetBytes(10_000);

        var restored = await DecryptAsync(await EncryptAsync(plaintext, Key(), chunkSize: 1024), Key());

        restored.Should().Equal(plaintext);
    }

    [Fact]
    public async Task Round_trips_data_that_exactly_fills_a_chunk()
    {
        // Off-by-one territory: the encryptor stops on a short read, so an exact multiple must not
        // silently drop the final chunk.
        var plaintext = RandomNumberGenerator.GetBytes(2048);

        var restored = await DecryptAsync(await EncryptAsync(plaintext, Key(), chunkSize: 1024), Key());

        restored.Should().Equal(plaintext);
    }

    [Fact]
    public async Task Round_trips_an_empty_archive()
    {
        (await DecryptAsync(await EncryptAsync([], Key()), Key())).Should().BeEmpty();
    }

    [Fact]
    public async Task The_ciphertext_does_not_contain_the_plaintext()
    {
        var secret = Encoding.UTF8.GetBytes("PGPASSWORD=super-secret-value");

        var ciphertext = await EncryptAsync(secret, Key());

        Encoding.UTF8.GetString(ciphertext).Should().NotContain("super-secret-value");
    }

    [Fact]
    public async Task A_wrong_key_cannot_decrypt()
    {
        var ciphertext = await EncryptAsync(Encoding.UTF8.GetBytes("payload"), Key(seed: 1));

        var act = async () => await DecryptAsync(ciphertext, Key(seed: 2));

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task A_flipped_byte_is_detected()
    {
        var ciphertext = await EncryptAsync(RandomNumberGenerator.GetBytes(4096), Key(), chunkSize: 1024);
        ciphertext[^1] ^= 0xFF;   // corrupt the last chunk's payload

        var act = async () => await DecryptAsync(ciphertext, Key());

        await act.Should().ThrowAsync<CryptographicException>(
            "silently decoding corrupt data would let a restore overwrite live data with garbage");
    }

    [Fact]
    public async Task A_truncated_archive_is_detected()
    {
        var ciphertext = await EncryptAsync(RandomNumberGenerator.GetBytes(4096), Key(), chunkSize: 1024);

        var act = async () => await DecryptAsync(ciphertext[..(ciphertext.Length - 100)], Key());

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task Reordered_chunks_are_detected()
    {
        // Each chunk binds its index into the tag, so a swap can't pass as a valid archive.
        const int chunk = 16;
        var plaintext = RandomNumberGenerator.GetBytes(chunk * 2);
        var ciphertext = await EncryptAsync(plaintext, Key(), chunkSize: chunk);

        const int header = 6 + 1 + 4;          // MAGIC + VERSION + CHUNKSIZE
        var record = 12 + 4 + 16 + chunk;      // NONCE + LEN + TAG + CIPHERTEXT
        var swapped = ciphertext.ToArray();
        Array.Copy(ciphertext, header + record, swapped, header, record);
        Array.Copy(ciphertext, header, swapped, header + record, record);

        var act = async () => await DecryptAsync(swapped, Key());

        await act.Should().ThrowAsync<CryptographicException>();
    }

    [Fact]
    public async Task A_foreign_file_is_rejected_rather_than_misread()
    {
        var notOurs = Encoding.UTF8.GetBytes("this is a plain gzip archive, not an encrypted one");

        var act = async () => await DecryptAsync(notOurs, Key());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a Harbora encrypted archive*");
    }

    [Fact]
    public async Task Encrypted_archives_are_recognised_and_plain_ones_are_not()
    {
        var dir = Directory.CreateTempSubdirectory("harbora-cipher");
        try
        {
            var encrypted = Path.Combine(dir.FullName, "a.enc");
            var plain = Path.Combine(dir.FullName, "b.tgz");
            await File.WriteAllBytesAsync(encrypted, await EncryptAsync(Encoding.UTF8.GetBytes("x"), Key()));
            await File.WriteAllBytesAsync(plain, Encoding.UTF8.GetBytes("not encrypted"));

            // Detection is what lets pre-encryption backups keep restoring after the upgrade.
            (await ArchiveCipher.IsEncryptedArchiveAsync(encrypted, default)).Should().BeTrue();
            (await ArchiveCipher.IsEncryptedArchiveAsync(plain, default)).Should().BeFalse();
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task An_empty_file_is_not_mistaken_for_an_encrypted_archive()
    {
        var dir = Directory.CreateTempSubdirectory("harbora-cipher-empty");
        try
        {
            var path = Path.Combine(dir.FullName, "empty.tgz");
            await File.WriteAllBytesAsync(path, []);

            (await ArchiveCipher.IsEncryptedArchiveAsync(path, default)).Should().BeFalse();
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task The_same_plaintext_encrypts_differently_each_time()
    {
        var plaintext = Encoding.UTF8.GetBytes("identical nightly backup");

        var first = await EncryptAsync(plaintext, Key());
        var second = await EncryptAsync(plaintext, Key());

        first.Should().NotEqual(second, "a fresh nonce per chunk must prevent identical ciphertexts");
    }
}
