using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Encryption on the wire.
///
/// On the private Docker network, plaintext between an app and its database was a defensible trade.
/// The moment external access publishes a port, it stops being one: the password and every row after
/// it cross the internet where anyone on the path can read them, and nothing on the page said so.
/// </summary>
public class DatabaseTlsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Postgres_has_to_be_configured_because_it_ships_with_encryption_off()
    {
        DatabaseTls.NeedsConfiguring(ManagedServiceType.PostgreSql).Should().BeTrue();
    }

    [Theory]
    [InlineData(ManagedServiceType.MySql)]
    [InlineData(ManagedServiceType.MariaDb)]
    public void MariaDb_and_MySql_are_left_alone_because_they_already_encrypt(ManagedServiceType type)
    {
        // They make their own certificate at first start and negotiate TLS 1.3 with any client that
        // asks. Replacing that with one of ours would be work with no gain and a restart to pay.
        DatabaseTls.EncryptedByDefault(type).Should().BeTrue();
        DatabaseTls.NeedsConfiguring(type).Should().BeFalse();
        DatabaseTls.Available(type).Should().BeTrue();
    }

    [Theory]
    [InlineData(ManagedServiceType.Redis)]
    [InlineData(ManagedServiceType.MongoDb)]
    public void An_engine_that_cannot_encrypt_says_so_rather_than_claiming_it_can(ManagedServiceType type)
    {
        DatabaseTls.Available(type).Should().BeFalse();
        DatabaseTls.ConnectionParameter(type).Should().BeNull();
    }

    [Fact]
    public void The_certificate_lives_apart_from_the_data()
    {
        // Its own volume, so a re-provision keeps the certificate and the data volume stays data.
        DatabaseTls.VolumeName("harbora-svc-shop").Should().Be("harbora-svc-shop-certs");
    }

    [Fact]
    public void A_certificate_that_already_exists_is_not_replaced()
    {
        // This runs on every provision. Rewriting would change the certificate under any client
        // that had pinned it, on a restart nobody asked for.
        var command = string.Join(" ", DatabaseTls.PrepareCommand());

        command.Should().Contain("if [ ! -f");
        command.Should().Contain("server.crt");
    }

    [Fact]
    public void The_certificate_is_made_without_needing_openssl_on_the_image()
    {
        // The first version shelled out to openssl and failed on the server with "openssl: not
        // found" — the postgres image does not carry the binary. The alternatives were pulling an
        // image to run one command, or depending on one that happened to have it.
        var (certificate, key) = DatabaseTls.Generate("harbora-svc-shop", Now);

        certificate.Should().StartWith("-----BEGIN CERTIFICATE-----");
        key.Should().StartWith("-----BEGIN PRIVATE KEY-----");

        string.Join(" ", DatabaseTls.PrepareCommand()).Should().NotContain("openssl");
    }

    [Fact]
    public void The_key_travels_in_the_environment_and_never_in_the_command()
    {
        // A key on a container's command line is in `docker inspect` and the host process list.
        var (certificate, key) = DatabaseTls.Generate("harbora-svc-shop", Now);
        var command = string.Join(" ", DatabaseTls.PrepareCommand());

        command.Should().NotContain("BEGIN PRIVATE KEY");
        DatabaseTls.PrepareEnvironment(certificate, key).Values.Should().Contain(key);
    }

    [Fact]
    public void The_certificate_is_valid_now_and_for_years()
    {
        var (pem, _) = DatabaseTls.Generate("harbora-svc-shop", Now);
        using var certificate = System.Security.Cryptography.X509Certificates
            .X509Certificate2.CreateFromPem(pem);

        // Compared in UTC: X509Certificate2 hands these back in local time, so the same assertion
        // against Now.UtcDateTime passes or fails depending on the machine's time zone.
        certificate.NotBefore.ToUniversalTime().Should().BeBefore(Now.UtcDateTime);
        certificate.NotAfter.ToUniversalTime().Should().BeAfter(Now.UtcDateTime.AddYears(9));
        certificate.Subject.Should().Contain("harbora-svc-shop");
    }

    [Fact]
    public void The_key_is_only_readable_by_the_database()
    {
        // PostgreSQL refuses to start if its key is readable by anyone else, and the failure reads
        // like data corruption rather than a permissions problem.
        var command = string.Join(" ", DatabaseTls.PrepareCommand());

        command.Should().Contain("chmod 600");
        command.Should().Contain("chown 999:999");
    }

    [Fact]
    public void Permissions_are_applied_even_when_the_certificate_was_already_there()
    {
        // A volume restored from a backup, or written by an older version of this code, would
        // otherwise keep permissions that stop the database booting.
        var command = string.Join(" ", DatabaseTls.PrepareCommand());
        var guard = command.IndexOf("if [ ! -f", StringComparison.Ordinal);
        var closed = command.IndexOf("fi;", StringComparison.Ordinal);
        var chmod = command.IndexOf("chmod 600", StringComparison.Ordinal);

        guard.Should().BeGreaterThan(0);
        chmod.Should().BeGreaterThan(closed, "chmod must run outside the guard, not inside it");
    }



    [Fact]
    public void The_server_is_told_to_use_the_certificate_it_was_given()
    {
        var command = string.Join(" ", DatabaseTls.ServerCommand());

        command.Should().StartWith("postgres");
        command.Should().Contain("ssl=on");
        command.Should().Contain($"ssl_cert_file={DatabaseTls.MountPath}/server.crt");
        command.Should().Contain($"ssl_key_file={DatabaseTls.MountPath}/server.key");
    }

    [Fact]
    public void A_connection_string_insists_on_encryption_rather_than_preferring_it()
    {
        // "prefer" silently falls back to plaintext, which is the worst of the three: it encrypts
        // nothing and reads as though it does.
        DatabaseTls.ConnectionParameter(ManagedServiceType.PostgreSql).Should().Be("sslmode=require");
        DatabaseTls.ConnectionParameter(ManagedServiceType.MariaDb).Should().Be("ssl-mode=REQUIRED");

        foreach (var type in new[] { ManagedServiceType.PostgreSql, ManagedServiceType.MySql, ManagedServiceType.MariaDb })
            DatabaseTls.ConnectionParameter(type).Should().NotContain("prefer");
    }

    [Fact]
    public void The_parameter_reaches_the_connection_string()
    {
        var connection = DatabaseCredentialManager.ConnectionString(
            "PostgreSql", "shop.example.com", 15432, "hb_reader", "secret", "shop",
            DatabaseTls.ConnectionParameter(ManagedServiceType.PostgreSql));

        connection.Should().Be("postgresql://hb_reader:secret@shop.example.com:15432/shop?sslmode=require");
    }

    [Fact]
    public void An_engine_that_cannot_encrypt_gets_no_promise_it_cannot_keep()
    {
        // sslmode=require against a server with SSL off does not warn — it refuses to connect. A
        // string that carries it anyway turns "unencrypted" into "broken".
        var connection = DatabaseCredentialManager.ConnectionString(
            "Redis", "cache.example.com", 15433, "hb_reader", "secret", "", null);

        connection.Should().NotContain("ssl");
    }

    [Fact]
    public void The_backfill_only_marks_the_engines_that_were_already_encrypted()
    {
        // The migration writes true for MySQL and MariaDB and leaves PostgreSQL false. Asserted
        // against the enum rather than the numbers in the SQL, because the numbers are what would
        // silently stop matching if the enum were ever reordered.
        ((int)ManagedServiceType.MySql).Should().Be(1);
        ((int)ManagedServiceType.MariaDb).Should().Be(2);
        ((int)ManagedServiceType.PostgreSql).Should().Be(0);

        DatabaseTls.EncryptedByDefault(ManagedServiceType.MySql).Should().BeTrue();
        DatabaseTls.EncryptedByDefault(ManagedServiceType.MariaDb).Should().BeTrue();
        DatabaseTls.EncryptedByDefault(ManagedServiceType.PostgreSql).Should().BeFalse();
    }

    [Fact]
    public void A_service_name_cannot_carry_anything_into_the_certificate_subject()
    {
        // The common name comes from a container name, which comes from a service name somebody
        // typed. It never reaches a shell now, but it does reach an X.500 name, where a stray comma
        // or equals sign would either move the value into another field or throw.
        var (pem, _) = DatabaseTls.Generate("shop, O=Somebody Else'; rm -rf /", Now);
        using var certificate = System.Security.Cryptography.X509Certificates
            .X509Certificate2.CreateFromPem(pem);

        // The letters survive as letters, which is harmless. What must not survive is the
        // punctuation that would move part of the value into another field of the name.
        certificate.Subject.Should().StartWith("CN=");
        certificate.Subject.Should().NotContain(",");
        certificate.Subject.Should().NotContain("'");
        certificate.Subject[3..].Should().NotContain("=");
    }
}
