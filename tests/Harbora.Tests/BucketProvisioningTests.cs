using FluentAssertions;
using Harbora.Infrastructure.Storage;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Provisioning a bucket: the policy that scopes its key, and the commands that create it.
///
/// The policy is the document deciding whether a leaked key is one bucket or the whole platform.
/// The commands run in a throwaway container out of the storage server's own image, and carry a
/// root password, an access key and a policy document as arguments — every one of which is a value
/// an interpolated script would hand to a shell.
/// </summary>
public class BucketProvisioningTests
{
    // --- the policy ---

    [Fact]
    public void A_policy_reaches_its_own_bucket_and_the_objects_in_it()
    {
        // Both lines are required and people routinely write only the second. Without the bare
        // bucket ARN a client can read and write by exact key but cannot list, which fails in a way
        // that reads as "the credentials are wrong".
        var policy = BucketPolicy.For("uploads");

        policy.Should().Contain("arn:aws:s3:::uploads\"");
        policy.Should().Contain("arn:aws:s3:::uploads/*");
    }

    [Fact]
    public void A_policy_reaches_nothing_else()
    {
        // The property that actually matters. A resource of "arn:aws:s3:::*" looks almost identical
        // and grants every tenant's objects to whoever holds this key.
        BucketPolicy.GrantsOnly(BucketPolicy.For("uploads"), "uploads").Should().BeTrue();
    }

    [Fact]
    public void A_wildcard_policy_would_be_recognised_as_one()
    {
        // The check has to be able to fail, or it is not a check.
        const string wildcard = """
            {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:*"],"Resource":["arn:aws:s3:::*"]}]}
            """;

        BucketPolicy.GrantsOnly(wildcard, "uploads").Should().BeFalse();
    }

    [Fact]
    public void Another_buckets_policy_is_not_this_buckets_policy()
    {
        BucketPolicy.GrantsOnly(BucketPolicy.For("other"), "uploads").Should().BeFalse();
    }

    [Fact]
    public void A_statement_with_no_resource_at_all_is_not_treated_as_harmless()
    {
        // Skipping it reads as "nothing to check here", but a statement without a Resource is not a
        // statement granting nothing — it is a document this code does not understand, and passing
        // it is the one outcome that must not happen.
        const string odd = """
            {"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:*"]}]}
            """;

        BucketPolicy.GrantsOnly(odd, "uploads").Should().BeFalse();
    }

    [Fact]
    public void A_policy_is_never_built_for_something_that_is_not_a_bucket()
    {
        // A name that reached this far unchecked would be interpolated into an ARN, and an ARN is
        // matched by prefix in places.
        var build = () => BucketPolicy.For("Not A Bucket");

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_policy_name_is_derived_from_the_bucket()
    {
        BucketPolicy.NameFor("uploads").Should().Be("uploads-rw");
    }

    // --- the commands ---

    private const string JsonLine = "{\"prefix\":\"uploads\",\"size\":19,\"objects\":1}";

    private const string Nasty = "x\"; mc admin user add hb evil evilpass; echo \"";

    [Fact]
    public void No_value_ever_reaches_the_script_text()
    {
        // The whole safety argument. Root password, access key, secret and policy document are all
        // arguments; the script is a constant whatever they contain.
        var argv = BucketCommands.Provision(
            "http://minio:9000", "root", Nasty, "uploads", "key", Nasty,
            BucketPolicy.For("uploads"), "uploads-rw", "10B");

        argv[2].Should().NotContain(Nasty);
        argv.Should().Contain(Nasty);
    }

    [Fact]
    public void No_value_reaches_the_script_text_of_any_command()
    {
        // The same property for every command, not only the one it was first written for. A safe
        // Provision beside an interpolated Measure is not a safe class.
        IReadOnlyList<string>[] all =
        [
            BucketCommands.Provision("http://minio:9000", "root", Nasty, Nasty, "key", "sec",
                BucketPolicy.For("uploads"), "uploads-rw", ""),
            BucketCommands.Remove("http://minio:9000", "root", Nasty, Nasty, "key", "uploads-rw"),
            BucketCommands.Measure("http://minio:9000", "root", Nasty, Nasty)
        ];

        foreach (var argv in all)
        {
            argv[2].Should().NotContain(Nasty);
            argv.Should().Contain(Nasty);
        }
    }

    [Fact]
    public void The_script_runs_with_a_placeholder_argv_zero()
    {
        // `sh -c script name arg1 …` — the element after the script becomes $0. Getting this wrong
        // shifts every argument by one, so the endpoint becomes the alias name and the root
        // password becomes the endpoint.
        var argv = BucketCommands.Measure("http://minio:9000", "root", "pw", "uploads");

        argv[0].Should().Be("sh");
        argv[1].Should().Be("-c");
        argv[3].Should().Be("sh");
        argv[4].Should().Be("http://minio:9000");
    }

    [Fact]
    public void Removing_a_bucket_does_not_force_it()
    {
        // Emptying somebody's bucket is not something "delete the bucket" is allowed to mean. The
        // refusal is the storage server's, and it is reported rather than overridden.
        var argv = BucketCommands.Remove("http://minio:9000", "root", "pw", "uploads", "key", "uploads-rw");

        argv[2].Should().NotContain("--force");
    }

    [Fact]
    public void Removing_a_bucket_also_removes_the_key_that_could_reach_it()
    {
        // A bucket deleted while its user survives leaves a credential that works again the moment
        // somebody creates a bucket with the same name.
        var argv = BucketCommands.Remove("http://minio:9000", "root", "pw", "uploads", "key", "uploads-rw");

        argv[2].Should().Contain("admin user remove");
        argv[2].Should().Contain("admin policy rm");
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(1024, "1024B")]
    public void No_quota_is_no_argument_rather_than_a_quota_of_nothing(long bytes, string expected)
    {
        // "--size 0" is a quota of nothing, and a bucket nobody can write to is not what "no limit"
        // is supposed to produce.
        BucketCommands.QuotaArgument(bytes).Should().Be(expected);
    }

    // --- reading the measurement ---

    [Fact]
    public void The_total_is_read_from_what_mc_printed()
    {
        BucketCommands.ParseUsage("""{"status":"success","size":4096,"objects":3}""")
            .Should().Be(4096);
    }

    [Fact]
    public void The_last_figure_wins_because_mc_prints_the_total_last()
    {
        // Taking the first would report one prefix as the size of the whole bucket.
        var output = """
            {"status":"success","size":100,"path":"a"}
            {"status":"success","size":900,"path":"total"}
            """;

        BucketCommands.ParseUsage(output).Should().Be(900);
    }

    [Fact]
    public void A_line_that_is_not_json_is_skipped_rather_than_read_as_zero()
    {
        var output = "mc: warning: something\n{\"status\":\"success\",\"size\":512}\n";

        BucketCommands.ParseUsage(output).Should().Be(512);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mc: <ERROR> unable to reach the server")]
    public void Nothing_usable_is_unknown_rather_than_empty(string? output)
    {
        // A bucket reported as 0 B that holds a terabyte is the kind of number people plan against.
        BucketCommands.ParseUsage(output).Should().BeNull();
    }

    [Fact]
    public void A_bare_value_on_a_line_does_not_bring_the_whole_reading_down()
    {
        // mc prints progress and diagnostics alongside its JSON. A line holding a bare number
        // parses as JSON but is not an object, and asking it for a field throws something the JSON
        // catch does not cover — so the guard is what stops one stray line taking out the figure.
        var output = "42\n{\"status\":\"success\",\"size\":700}\n";

        BucketCommands.ParseUsage(output).Should().Be(700);
    }

    [Fact]
    public void Dockers_framing_bytes_do_not_make_the_measurement_unreadable()
    {
        // A container with no TTY has its output framed: every line arrives with a few control
        // bytes on the front. StorageMeasurement documents the same trap from the same source, and
        // observed it on a real server — requiring the line to start with a brace meant every
        // measurement came back unreadable and the bucket read as never measured.
        // The eight-byte stream header Docker puts in front of every frame.
        var header = new string([(char)1, (char)0, (char)0, (char)0, (char)0, (char)0, (char)0, (char)71]);
        var framed = header + JsonLine;

        BucketCommands.ParseUsage(framed).Should().Be(19);
    }

    [Fact]
    public void A_negative_size_is_not_a_measurement()
    {
        // It would be rendered as a bucket holding less than nothing, and it would come out of
        // AllocationReading as unmeasured anyway — so it is refused where the reason is known.
        BucketCommands.ParseUsage("""{"status":"success","size":-1}""").Should().BeNull();
    }

    [Fact]
    public void An_empty_bucket_measures_as_empty_rather_than_unknown()
    {
        // Zero is a fact when the server said it, and distinct from never having asked.
        BucketCommands.ParseUsage("""{"status":"success","size":0}""").Should().Be(0);
    }
}
