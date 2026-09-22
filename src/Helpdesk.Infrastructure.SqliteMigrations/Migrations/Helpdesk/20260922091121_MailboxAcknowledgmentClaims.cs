using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class MailboxAcknowledgmentClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AcknowledgmentAttempts",
                table: "InboundMessageReceipt",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "AcknowledgmentClaimExpiresUnixMilliseconds",
                table: "InboundMessageReceipt",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AcknowledgmentClaimId",
                table: "InboundMessageReceipt",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgmentErrorCode",
                table: "InboundMessageReceipt",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AcknowledgmentNextRetryUnixMilliseconds",
                table: "InboundMessageReceipt",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AcknowledgmentStatus",
                table: "InboundMessageReceipt",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgmentTargetFingerprint",
                table: "InboundMessageReceipt",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgmentAttempts",
                table: "InboundMessageReceipt");

            migrationBuilder.DropColumn(
                name: "AcknowledgmentClaimExpiresUnixMilliseconds",
                table: "InboundMessageReceipt");

            migrationBuilder.DropColumn(
                name: "AcknowledgmentClaimId",
                table: "InboundMessageReceipt");

            migrationBuilder.DropColumn(
                name: "AcknowledgmentErrorCode",
                table: "InboundMessageReceipt");

            migrationBuilder.DropColumn(
                name: "AcknowledgmentNextRetryUnixMilliseconds",
                table: "InboundMessageReceipt");

            migrationBuilder.DropColumn(
                name: "AcknowledgmentStatus",
                table: "InboundMessageReceipt");

            migrationBuilder.DropColumn(
                name: "AcknowledgmentTargetFingerprint",
                table: "InboundMessageReceipt");
        }
    }
}
