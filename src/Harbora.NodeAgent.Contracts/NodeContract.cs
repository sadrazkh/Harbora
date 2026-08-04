using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// Constants that pin the node ↔ control-plane contract. Everything the two sides must agree on
/// before they can exchange a single meaningful byte lives here, so a version skew is a value
/// comparison rather than an archaeology exercise.
/// </summary>
public static class NodeContract
{
    /// <summary>The protocol version this build speaks. Bumped only for breaking wire changes.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// Every version this build can still speak, newest first. Negotiation picks the highest entry
    /// the control plane also supports, which is what lets an old node keep working through a
    /// control-plane upgrade instead of dropping off the moment the panel is deployed.
    /// </summary>
    public static readonly IReadOnlyList<int> SupportedProtocolVersions = [1];

    /// <summary>Path of the enrollment endpoint, relative to the control-plane base URL.</summary>
    public const string EnrollmentPath = "api/node-agent/v1/enroll";

    /// <summary>Path used to renew the node credential before it expires.</summary>
    public const string CredentialRenewPath = "api/node-agent/v1/credential/renew";

    /// <summary>Path of the persistent outbound channel (ws/wss).</summary>
    public const string ChannelPath = "api/node-agent/v1/channel";

    /// <summary>Path of the TCP gateway a node dials outbound to publish a database endpoint.</summary>
    public const string TunnelPath = "api/node-agent/v1/tunnel";

    /// <summary>
    /// Clock skew tolerated on a command envelope before it is rejected as a replay. Wide enough
    /// for an unsynchronised VPS, narrow enough that a captured envelope is useless by the time
    /// anyone could reuse it.
    /// </summary>
    public static readonly TimeSpan CommandFreshnessWindow = TimeSpan.FromMinutes(5);

    /// <summary>Serializer settings both sides must use. Shared so a casing change cannot desync them.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}
