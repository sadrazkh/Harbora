using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harbora.Data.Migrations
{
    /// <inheritdoc />
    public partial class NodeAgentV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NodeCommands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeRowId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "text", nullable: false),
                    CommandId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Command = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Nonce = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequiredScope = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    IdempotentReplay = table.Column<bool>(type: "boolean", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    IssuedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IssuedByName = table.Column<string>(type: "text", nullable: true),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceIp = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeCommands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NodeEnrollmentTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Prefix = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedByNodeId = table.Column<string>(type: "text", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeNameHint = table.Column<string>(type: "text", nullable: true),
                    Region = table.Column<string>(type: "text", nullable: true),
                    Environment = table.Column<string>(type: "text", nullable: true),
                    LabelsJson = table.Column<string>(type: "text", nullable: false),
                    ScopesJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeEnrollmentTokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NodeEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeRowId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Message = table.Column<string>(type: "text", nullable: false),
                    WorkloadId = table.Column<string>(type: "text", nullable: true),
                    DataJson = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NodeEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Nodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    MachineFingerprint = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Health = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Draining = table.Column<bool>(type: "boolean", nullable: false),
                    AgentVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProtocolVersion = table.Column<int>(type: "integer", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: true),
                    Environment = table.Column<string>(type: "text", nullable: true),
                    LabelsJson = table.Column<string>(type: "text", nullable: false),
                    OsName = table.Column<string>(type: "text", nullable: false),
                    OsVersion = table.Column<string>(type: "text", nullable: false),
                    KernelVersion = table.Column<string>(type: "text", nullable: false),
                    Architecture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ContainerRuntime = table.Column<string>(type: "text", nullable: false),
                    ContainerRuntimeVersion = table.Column<string>(type: "text", nullable: false),
                    CpuCores = table.Column<int>(type: "integer", nullable: false),
                    TotalMemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    TotalDiskBytes = table.Column<long>(type: "bigint", nullable: false),
                    FreeDiskBytes = table.Column<long>(type: "bigint", nullable: false),
                    FreeMemoryBytes = table.Column<long>(type: "bigint", nullable: false),
                    Load1 = table.Column<double>(type: "double precision", nullable: false),
                    IpAddressesJson = table.Column<string>(type: "text", nullable: false),
                    InventoryJson = table.Column<string>(type: "text", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "text", nullable: false),
                    RunningWorkloads = table.Column<int>(type: "integer", nullable: false),
                    ActiveDatabaseGrants = table.Column<int>(type: "integer", nullable: false),
                    ActiveTunnels = table.Column<int>(type: "integer", nullable: false),
                    CertificateThumbprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CertificateSerial = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CertificateNotAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CertificateGeneration = table.Column<int>(type: "integer", nullable: false),
                    GrantedScopesJson = table.Column<string>(type: "text", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "text", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResumeToken = table.Column<string>(type: "text", nullable: true),
                    LastReceivedSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastSentSequence = table.Column<long>(type: "bigint", nullable: false),
                    EnrolledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisconnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Nodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NodeCommands_CommandId",
                table: "NodeCommands",
                column: "CommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NodeCommands_IdempotencyKey",
                table: "NodeCommands",
                column: "IdempotencyKey");

            migrationBuilder.CreateIndex(
                name: "IX_NodeCommands_NodeId_Status",
                table: "NodeCommands",
                columns: new[] { "NodeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_NodeEnrollmentTokens_Prefix",
                table: "NodeEnrollmentTokens",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NodeEnrollmentTokens_TokenHash",
                table: "NodeEnrollmentTokens",
                column: "TokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_NodeEvents_NodeId_At",
                table: "NodeEvents",
                columns: new[] { "NodeId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_CertificateThumbprint",
                table: "Nodes",
                column: "CertificateThumbprint");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_MachineFingerprint",
                table: "Nodes",
                column: "MachineFingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_NodeId",
                table: "Nodes",
                column: "NodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Nodes_Status",
                table: "Nodes",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NodeCommands");

            migrationBuilder.DropTable(
                name: "NodeEnrollmentTokens");

            migrationBuilder.DropTable(
                name: "NodeEvents");

            migrationBuilder.DropTable(
                name: "Nodes");
        }
    }
}
