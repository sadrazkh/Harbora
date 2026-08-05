using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harbora.Infrastructure.Storage;

/// <summary>
/// The S3 policy attached to one bucket's user.
///
/// This is the document that decides whether a leaked key is one bucket or the whole platform, so
/// it is built here rather than written inline in a shell script: a resource of <c>arn:aws:s3:::*</c>
/// looks almost identical to the correct one and grants every tenant's objects to whoever holds the
/// key.
///
/// Two resource lines are required and people routinely write only one. <c>arn:…:bucket</c> covers
/// operations on the bucket itself — listing it — and <c>arn:…:bucket/*</c> covers the objects in
/// it. A policy with only the second lets a client write and read by exact key but not list, which
/// fails in a way that reads as "the credentials are wrong".
/// </summary>
public static class BucketPolicy
{
    /// <summary>The policy name for a bucket's user.</summary>
    public static string NameFor(string bucket) => $"{bucket}-rw";

    /// <summary>
    /// Full access to exactly one bucket and nothing else.
    /// </summary>
    public static string For(string bucket)
    {
        if (!BucketName.IsValid(bucket))
            throw new ArgumentException("A policy must not be built for a name that is not a bucket.", nameof(bucket));

        // Built through the JSON writer rather than by string concatenation. A bucket name cannot
        // contain a quote today, and a policy that would break if it ever could is not a thing to
        // leave lying around next to an authorisation decision.
        var document = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray
            {
                new JsonObject
                {
                    ["Effect"] = "Allow",
                    ["Action"] = new JsonArray { "s3:*" },
                    ["Resource"] = new JsonArray
                    {
                        $"arn:aws:s3:::{bucket}",
                        $"arn:aws:s3:::{bucket}/*"
                    }
                }
            }
        };

        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// Whether a policy document grants anything outside the named bucket.
    ///
    /// Used by the test suite as the property that actually matters, and kept here so the check and
    /// the document cannot drift apart.
    /// </summary>
    public static bool GrantsOnly(string policyJson, string bucket)
    {
        try
        {
            var root = JsonNode.Parse(policyJson)?.AsObject();
            if (root?["Statement"] is not JsonArray statements) return false;

            var allowed = new[] { $"arn:aws:s3:::{bucket}", $"arn:aws:s3:::{bucket}/*" };

            foreach (var statement in statements)
            {
                if (statement?["Resource"] is not JsonArray resources) return false;

                foreach (var resource in resources)
                    if (!allowed.Contains(resource?.GetValue<string>())) return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
