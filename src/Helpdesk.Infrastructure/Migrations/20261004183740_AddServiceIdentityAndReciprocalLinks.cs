using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceIdentityAndReciprocalLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OrchestrationLinkId",
                table: "Tickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OrchestrationLinkRevision",
                table: "Tickets",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrchestrationPeerInstanceId",
                table: "Tickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CredentialRevision",
                table: "M2MConnectivitySettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "DirectionId",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GrantHash",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkId",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LinkRevision",
                table: "M2MConnectivitySettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "LocalTenantId",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManagedSenderEnabled",
                table: "M2MConnectivitySettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "PeerInstanceId",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PeerTenantId",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceInstanceId",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceNamespaceId",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceLinkAttempts",
                columns: table => new
                {
                    AttemptId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    LocalTenantId = table.Column<string>(type: "text", nullable: false),
                    LocalActorId = table.Column<string>(type: "text", nullable: false),
                    PeerInstanceId = table.Column<string>(type: "text", nullable: false),
                    PeerTenantId = table.Column<string>(type: "text", nullable: true),
                    LinkId = table.Column<string>(type: "text", nullable: true),
                    ActiveRelationshipKey = table.Column<string>(type: "text", nullable: true),
                    LinkRevision = table.Column<long>(type: "bigint", nullable: false),
                    LifecycleState = table.Column<string>(type: "text", nullable: false),
                    Decision = table.Column<string>(type: "text", nullable: false),
                    CommitId = table.Column<string>(type: "text", nullable: true),
                    AbortId = table.Column<string>(type: "text", nullable: true),
                    RevocationId = table.Column<string>(type: "text", nullable: true),
                    DescriptorJson = table.Column<string>(type: "text", nullable: false),
                    DescriptorHash = table.Column<string>(type: "text", nullable: false),
                    GrantSummaryJson = table.Column<string>(type: "text", nullable: true),
                    GrantHash = table.Column<string>(type: "text", nullable: true),
                    ConsentId = table.Column<string>(type: "text", nullable: true),
                    ProtectedVerifier = table.Column<string>(type: "text", nullable: true),
                    ProtectedBrowserState = table.Column<string>(type: "text", nullable: true),
                    SessionBindingHash = table.Column<string>(type: "text", nullable: true),
                    PairingCodeHash = table.Column<string>(type: "text", nullable: true),
                    ProtectedPairingCode = table.Column<string>(type: "text", nullable: true),
                    ProtectedInboundEscrow = table.Column<string>(type: "text", nullable: true),
                    ProtectedExchangeResponse = table.Column<string>(type: "text", nullable: true),
                    ExchangeFingerprint = table.Column<string>(type: "text", nullable: true),
                    ExchangeResponseHash = table.Column<string>(type: "text", nullable: true),
                    ExchangeDispatched = table.Column<bool>(type: "boolean", nullable: false),
                    InboundPrincipalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProtectedOutboundCredential = table.Column<string>(type: "text", nullable: true),
                    OutboundProfileRevision = table.Column<int>(type: "integer", nullable: true),
                    PeerPreparedAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    LocalPreparedAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    LocalInboundActive = table.Column<bool>(type: "boolean", nullable: false),
                    LocalBusinessSenderEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PeerActiveAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    LocalActiveAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    PeerRevocationAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                    InitiatorVerificationReceiptId = table.Column<string>(type: "text", nullable: true),
                    ResponderVerificationReceiptId = table.Column<string>(type: "text", nullable: true),
                    LastErrorCode = table.Column<string>(type: "text", nullable: true),
                    ExpiresAtUnixSeconds = table.Column<long>(type: "bigint", nullable: false),
                    TerminalControlExpiresAtUnixSeconds = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUnixSeconds = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUnixSeconds = table.Column<long>(type: "bigint", nullable: false),
                    NextWorkAtUnixSeconds = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLinkAttempts", x => x.AttemptId);
                });

            migrationBuilder.CreateTable(
                name: "ServiceLinkOperations",
                columns: table => new
                {
                    LinkId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OperationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "text", nullable: false),
                    ResponseJson = table.Column<string>(type: "text", nullable: false),
                    ProtectedRequestJson = table.Column<string>(type: "text", nullable: true),
                    Outbound = table.Column<bool>(type: "boolean", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUnixSeconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLinkOperations", x => new { x.LinkId, x.OperationId });
                });

            migrationBuilder.CreateTable(
                name: "ServiceLinkRotations",
                columns: table => new
                {
                    RotationId = table.Column<string>(type: "text", nullable: false),
                    LinkId = table.Column<string>(type: "text", nullable: false),
                    DirectionId = table.Column<string>(type: "text", nullable: false),
                    IsIssuer = table.Column<bool>(type: "boolean", nullable: false),
                    RotationState = table.Column<string>(type: "text", nullable: false),
                    ActiveRotationKey = table.Column<string>(type: "text", nullable: true),
                    ExpectedCurrentCredentialRevision = table.Column<long>(type: "bigint", nullable: false),
                    SuccessorCredentialRevision = table.Column<long>(type: "bigint", nullable: true),
                    ProtectedOffer = table.Column<string>(type: "text", nullable: true),
                    ProtectedCandidate = table.Column<string>(type: "text", nullable: true),
                    OfferExpiresAtUnixSeconds = table.Column<long>(type: "bigint", nullable: true),
                    SuccessorVerificationReceiptId = table.Column<string>(type: "text", nullable: true),
                    ActivateDecisionId = table.Column<string>(type: "text", nullable: true),
                    CallerSwitchRevision = table.Column<long>(type: "bigint", nullable: true),
                    PredecessorRetireAtUnixSeconds = table.Column<long>(type: "bigint", nullable: true),
                    LastErrorCode = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUnixSeconds = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLinkRotations", x => x.RotationId);
                });

            migrationBuilder.CreateTable(
                name: "ServiceLinkVerificationReceipts",
                columns: table => new
                {
                    VerificationReceiptId = table.Column<string>(type: "text", nullable: false),
                    LinkId = table.Column<string>(type: "text", nullable: false),
                    AttemptId = table.Column<string>(type: "text", nullable: false),
                    GrantHash = table.Column<string>(type: "text", nullable: false),
                    ServicePrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    DirectionId = table.Column<string>(type: "text", nullable: false),
                    CredentialRevision = table.Column<long>(type: "bigint", nullable: false),
                    RotationId = table.Column<string>(type: "text", nullable: true),
                    VerifiedAtUnixSeconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLinkVerificationReceipts", x => x.VerificationReceiptId);
                });

            migrationBuilder.CreateTable(
                name: "ServicePrincipalRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedClientId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PeerInstanceId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PeerTenantId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AllowedScopesJson = table.Column<string>(type: "text", nullable: false),
                    CustomerIdsJson = table.Column<string>(type: "text", nullable: false),
                    ResourceConstraintsJson = table.Column<string>(type: "text", nullable: false),
                    SourceInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceNamespaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinkId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AttemptId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    GrantHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DescriptorHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DirectionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LinkRevision = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CurrentCredentialRevision = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    DeploymentFingerprint = table.Column<string>(type: "text", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TerminalControlUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePrincipalRegistrations", x => x.Id);
                    table.CheckConstraint("CK_ServicePrincipal_Status", "\"Status\" IN ('pending','prepared','verified','in_doubt','active','revoked','expired','failed')");
                    table.ForeignKey(
                        name: "FK_ServicePrincipalRegistrations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServiceSigningKeys",
                columns: table => new
                {
                    Kid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ProtectedPrivateKey = table.Column<string>(type: "text", nullable: false),
                    PublicModulus = table.Column<string>(type: "text", nullable: false),
                    PublicExponent = table.Column<string>(type: "text", nullable: false),
                    ActiveSlot = table.Column<int>(type: "integer", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidateUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceSigningKeys", x => x.Kid);
                });

            migrationBuilder.CreateTable(
                name: "ServicePrincipalSecrets",
                columns: table => new
                {
                    ServicePrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialRevision = table.Column<long>(type: "bigint", nullable: false),
                    SecretHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Salt = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetireAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePrincipalSecrets", x => new { x.ServicePrincipalId, x.CredentialRevision });
                    table.CheckConstraint("CK_ServiceSecret_Status", "\"Status\" IN ('pending','active','retiring','revoked')");
                    table.ForeignKey(
                        name: "FK_ServicePrincipalSecrets_ServicePrincipalRegistrations_Servi~",
                        column: x => x.ServicePrincipalId,
                        principalTable: "ServicePrincipalRegistrations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLinkAttempts_ActiveRelationshipKey",
                table: "ServiceLinkAttempts",
                column: "ActiveRelationshipKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLinkAttempts_LifecycleState_NextWorkAtUnixSeconds",
                table: "ServiceLinkAttempts",
                columns: new[] { "LifecycleState", "NextWorkAtUnixSeconds" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLinkAttempts_LinkId",
                table: "ServiceLinkAttempts",
                column: "LinkId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLinkAttempts_PeerInstanceId_LocalTenantId_PeerTenant~",
                table: "ServiceLinkAttempts",
                columns: new[] { "PeerInstanceId", "LocalTenantId", "PeerTenantId", "LinkRevision", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLinkOperations_Outbound_Completed",
                table: "ServiceLinkOperations",
                columns: new[] { "Outbound", "Completed" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLinkRotations_ActiveRotationKey",
                table: "ServiceLinkRotations",
                column: "ActiveRotationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLinkRotations_LinkId_DirectionId_ExpectedCurrentCred~",
                table: "ServiceLinkRotations",
                columns: new[] { "LinkId", "DirectionId", "ExpectedCurrentCredentialRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLinkVerificationReceipts_LinkId_ServicePrincipalId_C~",
                table: "ServiceLinkVerificationReceipts",
                columns: new[] { "LinkId", "ServicePrincipalId", "CredentialRevision", "RotationId" });

            migrationBuilder.CreateIndex(
                name: "IX_ServicePrincipalRegistrations_LinkId_DirectionId",
                table: "ServicePrincipalRegistrations",
                columns: new[] { "LinkId", "DirectionId" },
                unique: true,
                filter: "\"LinkId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePrincipalRegistrations_NormalizedClientId",
                table: "ServicePrincipalRegistrations",
                column: "NormalizedClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServicePrincipalRegistrations_OrganizationId",
                table: "ServicePrincipalRegistrations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ServicePrincipalSecrets_ServicePrincipalId",
                table: "ServicePrincipalSecrets",
                column: "ServicePrincipalId",
                unique: true,
                filter: "\"Status\" = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceSigningKeys_ActiveSlot",
                table: "ServiceSigningKeys",
                column: "ActiveSlot",
                unique: true,
                filter: "\"ActiveSlot\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServiceLinkAttempts");

            migrationBuilder.DropTable(
                name: "ServiceLinkOperations");

            migrationBuilder.DropTable(
                name: "ServiceLinkRotations");

            migrationBuilder.DropTable(
                name: "ServiceLinkVerificationReceipts");

            migrationBuilder.DropTable(
                name: "ServicePrincipalSecrets");

            migrationBuilder.DropTable(
                name: "ServiceSigningKeys");

            migrationBuilder.DropTable(
                name: "ServicePrincipalRegistrations");

            migrationBuilder.DropColumn(
                name: "OrchestrationLinkId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "OrchestrationLinkRevision",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "OrchestrationPeerInstanceId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "CredentialRevision",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "DirectionId",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "GrantHash",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "LinkId",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "LinkRevision",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "LocalTenantId",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "ManagedSenderEnabled",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "PeerInstanceId",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "PeerTenantId",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "SourceInstanceId",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "SourceNamespaceId",
                table: "M2MConnectivitySettings");
        }
    }
}
