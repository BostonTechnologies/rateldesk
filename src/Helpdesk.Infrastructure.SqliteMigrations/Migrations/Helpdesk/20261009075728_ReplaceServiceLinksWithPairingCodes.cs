using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class ReplaceServiceLinksWithPairingCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "IncidentReceiverPrincipalBindings" SET "IsEnabled" = FALSE
                WHERE "PrincipalKind" = 'service_principal';
                UPDATE "IncidentReceiverSources" SET "IsEnabled" = FALSE
                WHERE EXISTS (SELECT 1 FROM "IncidentReceiverPrincipalBindings" b
                              WHERE b."SourceNamespaceId" = "IncidentReceiverSources"."SourceNamespaceId"
                                AND b."PrincipalKind" = 'service_principal')
                  AND NOT EXISTS (SELECT 1 FROM "IncidentReceiverPrincipalBindings" b
                                  WHERE b."SourceNamespaceId" = "IncidentReceiverSources"."SourceNamespaceId"
                                    AND b."IsEnabled" = TRUE);
                UPDATE "ServicePrincipalSecrets" SET "Status" = 'revoked';
                UPDATE "ServicePrincipalRegistrations"
                SET "Status" = 'revoked', "RevokedAtUtc" = COALESCE("RevokedAtUtc", CURRENT_TIMESTAMP),
                    "TerminalControlUntilUtc" = NULL, "Revision" = "Revision" + 1, "Version" = "Version" + 1;
                UPDATE "AutomationBindings" SET "Enabled" = FALSE;
                UPDATE "M2MConnectivitySettings" SET "ProtectedClientSecret" = NULL, "Enabled" = FALSE,
                    "ManagedSenderEnabled" = FALSE;
                """);
            migrationBuilder.DropTable(
                name: "M2MConnectivitySettings");

            migrationBuilder.DropTable(
                name: "ServiceLinkAttempts");

            migrationBuilder.DropTable(
                name: "ServiceLinkOperations");

            migrationBuilder.DropTable(
                name: "ServiceLinkRotations");

            migrationBuilder.DropTable(
                name: "ServiceLinkVerificationReceipts");

            migrationBuilder.DropIndex(
                name: "IX_ServicePrincipalRegistrations_LinkId_DirectionId",
                table: "ServicePrincipalRegistrations");

            migrationBuilder.DropIndex(
                name: "IX_IncidentReceiverSources_SourceInstanceId",
                table: "IncidentReceiverSources");

            migrationBuilder.DropColumn(
                name: "AttemptId",
                table: "ServicePrincipalRegistrations");

            migrationBuilder.DropColumn(
                name: "DescriptorHash",
                table: "ServicePrincipalRegistrations");

            migrationBuilder.DropColumn(
                name: "DirectionId",
                table: "ServicePrincipalRegistrations");

            migrationBuilder.DropColumn(
                name: "GrantHash",
                table: "ServicePrincipalRegistrations");

            migrationBuilder.DropColumn(
                name: "LinkId",
                table: "ServicePrincipalRegistrations");

            migrationBuilder.RenameColumn(
                name: "LinkRevision",
                table: "ServicePrincipalRegistrations",
                newName: "MappingRevision");

            migrationBuilder.AddColumn<Guid>(
                name: "MappingId",
                table: "ServicePrincipalRegistrations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SystemConnectionId",
                table: "AutomationBindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InstallationPairingCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Salt = table.Column<string>(type: "TEXT", nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConsumedOperationId = table.Column<string>(type: "TEXT", nullable: true),
                    ConsumedPeerId = table.Column<string>(type: "TEXT", nullable: true),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallationPairingCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PairingCleanups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtectedRequest = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingCleanups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PairingRedemptions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PairId = table.Column<string>(type: "TEXT", nullable: false),
                    PairGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestHash = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedResponse = table.Column<string>(type: "TEXT", nullable: true),
                    RetryUntilUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairingRedemptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PairId = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    MappingJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    PairGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    InboundPrincipalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProtectedInboundCredential = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedOutboundCredential = table.Column<string>(type: "TEXT", nullable: true),
                    SaveOperationId = table.Column<string>(type: "TEXT", nullable: true),
                    SaveRequestHash = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedSaveResponse = table.Column<string>(type: "TEXT", nullable: true),
                    LastTestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastTestSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                    LastTestMessage = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemPairs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PeerInstallationId = table.Column<string>(type: "TEXT", nullable: false),
                    PeerJson = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    Salt = table.Column<string>(type: "TEXT", nullable: false),
                    InboundSecretHash = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedInboundSecret = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedOutboundSecret = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ConnectOperationId = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedExchangeRequest = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemPairs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePrincipalRegistrations_MappingId",
                table: "ServicePrincipalRegistrations",
                column: "MappingId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentReceiverSources_SourceInstanceId",
                table: "IncidentReceiverSources",
                column: "SourceInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_PairingCleanups_ExpiresAtUtc",
                table: "PairingCleanups",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PairingRedemptions_PairId",
                table: "PairingRedemptions",
                column: "PairId");

            migrationBuilder.CreateIndex(
                name: "IX_PairingRedemptions_RetryUntilUtc",
                table: "PairingRedemptions",
                column: "RetryUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SystemConnections_PairId",
                table: "SystemConnections",
                column: "PairId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemPairs_PeerInstallationId",
                table: "SystemPairs",
                column: "PeerInstallationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Pairing-code cutover is forward-only. Restore the pre-upgrade database and persistent keys backup to return to the previous product version.");
        }
    }
}
