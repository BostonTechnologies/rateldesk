using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class MultiProviderMailboxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EmailInboxSettings_MailboxAddress",
                table: "EmailInboxSettings");

            migrationBuilder.AddColumn<bool>(
                name: "Archived",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "Authentication",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BatchSize",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 25);

            migrationBuilder.AddColumn<int>(
                name: "CredentialVersion",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "EmailInboxSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "InitialImport",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "LegacySource",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MarkReadAfterSuccess",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganizationId",
                table: "EmailInboxSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Password",
                table: "EmailInboxSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PollIntervalSeconds",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "ProcessedFolder",
                table: "EmailInboxSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Provider",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SourceKey",
                table: "EmailInboxSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "TlsMode",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Username",
                table: "EmailInboxSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "EmailInboxSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "InboundMessageReceipt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TransportKey = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ConfigurationVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    InternetMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    OrganizationId = table.Column<string>(type: "TEXT", nullable: true),
                    TicketId = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: true),
                    Acknowledged = table.Column<bool>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    ProtectedEnvelope = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboundMessageReceipt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InboundMessageReceipt_EmailInboxSettings_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "EmailInboxSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MailboxIngestionState",
                columns: table => new
                {
                    MailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    Cursor = table.Column<string>(type: "TEXT", nullable: true),
                    Initialized = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastTestUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    TestedVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSyncUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    NextRetryUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailboxIngestionState", x => x.MailboxId);
                    table.ForeignKey(
                        name: "FK_MailboxIngestionState_EmailInboxSettings_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "EmailInboxSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MailboxLease",
                columns: table => new
                {
                    MailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", nullable: true),
                    Fence = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailboxLease", x => x.MailboxId);
                    table.ForeignKey(
                        name: "FK_MailboxLease_EmailInboxSettings_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "EmailInboxSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MailboxMigrationState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    OutboundMailboxAddress = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailboxMigrationState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MailboxOutboxEffect",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EffectKey = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Fence = table.Column<long>(type: "INTEGER", nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AvailableUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    LeaseExpiresUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    DeliveryEventId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailboxOutboxEffect", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailInboxSettings_MailboxAddress",
                table: "EmailInboxSettings",
                column: "MailboxAddress",
                unique: true,
                filter: "\"Archived\" = false AND \"Enabled\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_EmailInboxSettings_OrganizationId",
                table: "EmailInboxSettings",
                column: "OrganizationId",
                unique: true,
                filter: "\"Archived\" = false AND \"Scope\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_EmailInboxSettings_Scope",
                table: "EmailInboxSettings",
                column: "Scope",
                unique: true,
                filter: "\"Archived\" = false AND \"Scope\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_EmailInboxSettings_SourceKey",
                table: "EmailInboxSettings",
                column: "SourceKey",
                unique: true,
                filter: "\"Archived\" = false AND \"Enabled\" = true");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Mailbox_Assignment",
                table: "EmailInboxSettings",
                sql: "(\"Scope\" = 0 AND \"OrganizationId\" IS NULL) OR (\"Scope\" = 1 AND \"OrganizationId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessageReceipt_MailboxId_Outcome_Acknowledged",
                table: "InboundMessageReceipt",
                columns: new[] { "MailboxId", "Outcome", "Acknowledged" });

            migrationBuilder.CreateIndex(
                name: "IX_InboundMessageReceipt_MailboxId_SourceKey_TransportKey",
                table: "InboundMessageReceipt",
                columns: new[] { "MailboxId", "SourceKey", "TransportKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailboxOutboxEffect_DeliveryEventId",
                table: "MailboxOutboxEffect",
                column: "DeliveryEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailboxOutboxEffect_ReceiptId_EffectKey",
                table: "MailboxOutboxEffect",
                columns: new[] { "ReceiptId", "EffectKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailboxOutboxEffect_State_AvailableUnixMilliseconds",
                table: "MailboxOutboxEffect",
                columns: new[] { "State", "AvailableUnixMilliseconds" });

            migrationBuilder.CreateIndex(
                name: "IX_MailboxOutboxEffect_State_LeaseExpiresUnixMilliseconds",
                table: "MailboxOutboxEffect",
                columns: new[] { "State", "LeaseExpiresUnixMilliseconds" });

            migrationBuilder.AddForeignKey(
                name: "FK_EmailInboxSettings_Organizations_OrganizationId",
                table: "EmailInboxSettings",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException("Restore the pre-upgrade database and key-ring backup; mailbox receipt rollback is not lossless.");
        }
    }
}
