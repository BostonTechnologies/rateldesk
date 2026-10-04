using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OrchestrationLinkRevision",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrchestrationPeerInstanceId",
                table: "Tickets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CredentialRevision",
                table: "M2MConnectivitySettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "DirectionId",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GrantHash",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkId",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LinkRevision",
                table: "M2MConnectivitySettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "LocalTenantId",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManagedSenderEnabled",
                table: "M2MConnectivitySettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "PeerInstanceId",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PeerTenantId",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceInstanceId",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceNamespaceId",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ServiceLinkAttempts",
                columns: table => new
                {
                    AttemptId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    LocalTenantId = table.Column<string>(type: "TEXT", nullable: false),
                    LocalActorId = table.Column<string>(type: "TEXT", nullable: false),
                    PeerInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    PeerTenantId = table.Column<string>(type: "TEXT", nullable: true),
                    LinkId = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveRelationshipKey = table.Column<string>(type: "TEXT", nullable: true),
                    LinkRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    LifecycleState = table.Column<string>(type: "TEXT", nullable: false),
                    Decision = table.Column<string>(type: "TEXT", nullable: false),
                    CommitId = table.Column<string>(type: "TEXT", nullable: true),
                    AbortId = table.Column<string>(type: "TEXT", nullable: true),
                    RevocationId = table.Column<string>(type: "TEXT", nullable: true),
                    DescriptorJson = table.Column<string>(type: "TEXT", nullable: false),
                    DescriptorHash = table.Column<string>(type: "TEXT", nullable: false),
                    GrantSummaryJson = table.Column<string>(type: "TEXT", nullable: true),
                    GrantHash = table.Column<string>(type: "TEXT", nullable: true),
                    ConsentId = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedVerifier = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedBrowserState = table.Column<string>(type: "TEXT", nullable: true),
                    SessionBindingHash = table.Column<string>(type: "TEXT", nullable: true),
                    PairingCodeHash = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedPairingCode = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedInboundEscrow = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedExchangeResponse = table.Column<string>(type: "TEXT", nullable: true),
                    ExchangeFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    ExchangeResponseHash = table.Column<string>(type: "TEXT", nullable: true),
                    ExchangeDispatched = table.Column<bool>(type: "INTEGER", nullable: false),
                    InboundPrincipalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProtectedOutboundCredential = table.Column<string>(type: "TEXT", nullable: true),
                    OutboundProfileRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    PeerPreparedAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    LocalPreparedAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    LocalInboundActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LocalBusinessSenderEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PeerActiveAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    LocalActiveAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    PeerRevocationAcknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    InitiatorVerificationReceiptId = table.Column<string>(type: "TEXT", nullable: true),
                    ResponderVerificationReceiptId = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ExpiresAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    TerminalControlExpiresAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    NextWorkAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLinkAttempts", x => x.AttemptId);
                });

            migrationBuilder.CreateTable(
                name: "ServiceLinkOperations",
                columns: table => new
                {
                    LinkId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedRequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    Outbound = table.Column<bool>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLinkOperations", x => new { x.LinkId, x.OperationId });
                });

            migrationBuilder.CreateTable(
                name: "ServiceLinkRotations",
                columns: table => new
                {
                    RotationId = table.Column<string>(type: "TEXT", nullable: false),
                    LinkId = table.Column<string>(type: "TEXT", nullable: false),
                    DirectionId = table.Column<string>(type: "TEXT", nullable: false),
                    IsIssuer = table.Column<bool>(type: "INTEGER", nullable: false),
                    RotationState = table.Column<string>(type: "TEXT", nullable: false),
                    ActiveRotationKey = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedCurrentCredentialRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    SuccessorCredentialRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    ProtectedOffer = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedCandidate = table.Column<string>(type: "TEXT", nullable: true),
                    OfferExpiresAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    SuccessorVerificationReceiptId = table.Column<string>(type: "TEXT", nullable: true),
                    ActivateDecisionId = table.Column<string>(type: "TEXT", nullable: true),
                    CallerSwitchRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    PredecessorRetireAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLinkRotations", x => x.RotationId);
                });

            migrationBuilder.CreateTable(
                name: "ServiceLinkVerificationReceipts",
                columns: table => new
                {
                    VerificationReceiptId = table.Column<string>(type: "TEXT", nullable: false),
                    LinkId = table.Column<string>(type: "TEXT", nullable: false),
                    AttemptId = table.Column<string>(type: "TEXT", nullable: false),
                    GrantHash = table.Column<string>(type: "TEXT", nullable: false),
                    ServicePrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DirectionId = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RotationId = table.Column<string>(type: "TEXT", nullable: true),
                    VerifiedAtUnixSeconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceLinkVerificationReceipts", x => x.VerificationReceiptId);
                });

            migrationBuilder.CreateTable(
                name: "ServicePrincipalRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    NormalizedClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OrganizationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PeerInstanceId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PeerTenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AllowedScopesJson = table.Column<string>(type: "TEXT", nullable: false),
                    CustomerIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceConstraintsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceInstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceNamespaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LinkId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AttemptId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    GrantHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DescriptorHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DirectionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LinkRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CurrentCredentialRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    DeploymentFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    TerminalControlUntilUtc = table.Column<long>(type: "INTEGER", nullable: true)
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
                    Kid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ProtectedPrivateKey = table.Column<string>(type: "TEXT", nullable: false),
                    PublicModulus = table.Column<string>(type: "TEXT", nullable: false),
                    PublicExponent = table.Column<string>(type: "TEXT", nullable: false),
                    ActiveSlot = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ValidateUntilUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceSigningKeys", x => x.Kid);
                });

            migrationBuilder.CreateTable(
                name: "ServicePrincipalSecrets",
                columns: table => new
                {
                    ServicePrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CredentialRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Salt = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RetireAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServicePrincipalSecrets", x => new { x.ServicePrincipalId, x.CredentialRevision });
                    table.CheckConstraint("CK_ServiceSecret_Status", "\"Status\" IN ('pending','active','retiring','revoked')");
                    table.ForeignKey(
                        name: "FK_ServicePrincipalSecrets_ServicePrincipalRegistrations_ServicePrincipalId",
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
                name: "IX_ServiceLinkAttempts_PeerInstanceId_LocalTenantId_PeerTenantId_LinkRevision_Role",
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
                name: "IX_ServiceLinkRotations_LinkId_DirectionId_ExpectedCurrentCredentialRevision",
                table: "ServiceLinkRotations",
                columns: new[] { "LinkId", "DirectionId", "ExpectedCurrentCredentialRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceLinkVerificationReceipts_LinkId_ServicePrincipalId_CredentialRevision_RotationId",
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
