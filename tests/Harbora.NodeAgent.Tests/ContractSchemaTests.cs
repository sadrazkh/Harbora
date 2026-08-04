using System.Text.Json;
using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Keeps the published JSON Schema and the C# mirror of it honest about each other.
///
/// <para>
/// The control plane is written against the schema and the node against the records. Nothing at
/// compile time connects the two, so without these tests a renamed field is a runtime surprise on
/// a customer's server rather than a red build here.
/// </para>
/// </summary>
public class ContractSchemaTests
{
    private static readonly JsonDocument Schema = LoadSchema();

    private static JsonDocument LoadSchema()
    {
        var path = Path.Combine(RepoPaths.ContractV1, "node-agent.v1.schema.json");
        File.Exists(path).Should().BeTrue($"the v1 contract schema must exist at {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static JsonElement Def(string name)
    {
        Schema.RootElement.GetProperty("$defs").TryGetProperty(name, out var def)
            .Should().BeTrue($"the schema must define $defs/{name}");
        return def;
    }

    private static IEnumerable<string> EnumValues(JsonElement element) =>
        element.GetProperty("enum").EnumerateArray().Select(v => v.GetString()!);

    [Fact]
    public void Schema_is_valid_json_and_declares_draft_2020_12()
    {
        Schema.RootElement.GetProperty("$schema").GetString()
            .Should().Be("https://json-schema.org/draft/2020-12/schema");
    }

    [Fact]
    public void Command_allowlist_matches_the_catalog_exactly()
    {
        var schemaCommands = EnumValues(Def("commandName")).OrderBy(x => x, StringComparer.Ordinal);
        var codeCommands = NodeCommandCatalog.All.OrderBy(x => x, StringComparer.Ordinal);

        schemaCommands.Should().Equal(codeCommands,
            "a verb the panel may send but the node does not implement — or vice versa — is a silent hole in the allowlist");
    }

    [Fact]
    public void Allowlist_contains_no_arbitrary_execution_verb()
    {
        // The single most important property of this contract, asserted rather than assumed.
        var forbidden = new[] { "shell", "exec", "run", "eval", "script", "command" };

        foreach (var command in NodeCommandCatalog.All)
        foreach (var word in forbidden)
        {
            // "GetWorkloadStatus" is fine; "RunShell" or "ExecCommand" would not be.
            var isExecVerb = command.StartsWith(word, StringComparison.OrdinalIgnoreCase);
            isExecVerb.Should().BeFalse($"'{command}' looks like an arbitrary-execution verb");
        }
    }

    [Fact]
    public void Scopes_match_the_catalog()
    {
        var schemaScopes = EnumValues(Def("scope")).OrderBy(x => x, StringComparer.Ordinal);
        var codeScopes = NodeScopes.Default.OrderBy(x => x, StringComparer.Ordinal);

        schemaScopes.Should().Equal(codeScopes);
    }

    [Fact]
    public void Every_command_declares_a_scope_the_schema_knows()
    {
        var schemaScopes = EnumValues(Def("scope")).ToHashSet(StringComparer.Ordinal);

        foreach (var command in NodeCommandCatalog.All)
        {
            NodeCommandCatalog.TryGet(command, out var descriptor).Should().BeTrue();
            schemaScopes.Should().Contain(descriptor.RequiredScope, $"{command} requires it");
        }
    }

    [Fact]
    public void Error_codes_match_the_enum()
    {
        var schemaCodes = EnumValues(Def("errorCode")).OrderBy(x => x, StringComparer.Ordinal);

        var codeCodes = Enum.GetNames<NodeErrorCode>()
            .Select(n => JsonNamingPolicy.CamelCase.ConvertName(n))
            .OrderBy(x => x, StringComparer.Ordinal);

        schemaCodes.Should().Equal(codeCodes,
            "the panel branches on these strings; an unlisted code reaches it as an unhandled default");
    }

    [Fact]
    public void Frame_types_match_the_constants()
    {
        var schemaTypes = EnumValues(Def("controlFrame").GetProperty("properties").GetProperty("type"))
            .ToHashSet(StringComparer.Ordinal);

        string[] declared =
        [
            NodeFrames.Hello, NodeFrames.Resume, NodeFrames.Heartbeat, NodeFrames.Inventory,
            NodeFrames.CommandAck, NodeFrames.CommandProgress, NodeFrames.CommandResult,
            NodeFrames.LogChunk, NodeFrames.Event, NodeFrames.Pong,
            ControlFrames.HelloAck, ControlFrames.Command, ControlFrames.Cancel,
            ControlFrames.CredentialRotated, ControlFrames.Ack, ControlFrames.Ping,
        ];

        schemaTypes.Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void Database_engines_match_the_schema()
    {
        var schemaEngines = Def("databaseAccessGrantSpec")
            .GetProperty("properties").GetProperty("engine").GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()!)
            .OrderBy(x => x, StringComparer.Ordinal);

        DatabaseEngines.All.OrderBy(x => x, StringComparer.Ordinal).Should().Equal(schemaEngines);
    }

    [Theory]
    [InlineData("commandName")]
    [InlineData("commandEnvelope")]
    [InlineData("commandAck")]
    [InlineData("commandProgress")]
    [InlineData("commandResult")]
    [InlineData("controlFrame")]
    [InlineData("enrollmentRequest")]
    [InlineData("enrollmentResponse")]
    [InlineData("credentialRenewalRequest")]
    [InlineData("credentialRenewalResponse")]
    [InlineData("nodeHello")]
    [InlineData("controlHelloAck")]
    [InlineData("nodeHeartbeat")]
    [InlineData("nodeInventory")]
    [InlineData("nodeCapabilities")]
    [InlineData("workloadSpec")]
    [InlineData("appManifest")]
    [InlineData("databaseAccessGrantSpec")]
    [InlineData("databaseAccessGrantState")]
    [InlineData("tunnelState")]
    [InlineData("tunnelRegistration")]
    [InlineData("agentUpdateRequest")]
    [InlineData("agentUpdateResult")]
    [InlineData("nodeError")]
    [InlineData("auditMetadata")]
    public void Required_contract_item_is_defined(string definition)
    {
        // Section 13 of the brief names each of these explicitly.
        Def(definition).ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void Image_reference_requires_a_digest()
    {
        var required = Def("imageRef").GetProperty("required").EnumerateArray().Select(v => v.GetString());
        required.Should().Contain("digest", "an unpinned image cannot express 'deploy what was tested'");
    }

    [Fact]
    public void Mount_spec_offers_no_host_path()
    {
        var properties = Def("mountSpec").GetProperty("properties");

        properties.TryGetProperty("hostPath", out _).Should().BeFalse();
        properties.TryGetProperty("source", out _).Should().BeFalse();
        properties.TryGetProperty("bind", out _).Should().BeFalse();
    }

    [Fact]
    public void Persistent_database_grant_requires_explicit_confirmation_and_an_allowlist()
    {
        var conditional = Def("databaseAccessGrantSpec").GetProperty("allOf");

        var persistentRule = conditional.EnumerateArray().First(rule =>
            rule.GetProperty("if").GetProperty("properties").GetProperty("mode")
                .GetProperty("const").GetString() == "persistent");

        var thenRequired = persistentRule.GetProperty("then").GetProperty("required")
            .EnumerateArray().Select(v => v.GetString()).ToList();

        thenRequired.Should().Contain("operatorConfirmed");
        thenRequired.Should().Contain("ipAllowlist");
    }

    [Fact]
    public void Temporary_database_grant_requires_a_ttl()
    {
        var conditional = Def("databaseAccessGrantSpec").GetProperty("allOf");

        var temporaryRule = conditional.EnumerateArray().First(rule =>
            rule.GetProperty("if").GetProperty("properties").GetProperty("mode")
                .GetProperty("const").GetString() == "temporary");

        temporaryRule.GetProperty("then").GetProperty("required")
            .EnumerateArray().Select(v => v.GetString())
            .Should().Contain("ttlSeconds", "a temporary grant with no end is a persistent grant with better branding");
    }

    [Fact]
    public void Agent_update_requires_a_checksum()
    {
        Def("agentUpdateRequest").GetProperty("required").EnumerateArray().Select(v => v.GetString())
            .Should().Contain("sha256");
    }
}
