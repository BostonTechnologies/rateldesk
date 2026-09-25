using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoricalMailboxImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BaselineCompletedUnixMilliseconds",
                table: "MailboxIngestionState",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HistoricalImportCompletedUnixMilliseconds",
                table: "InboundMessageReceipt",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HistoricalImportErrorCode",
                table: "InboundMessageReceipt",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HistoricalImportRequestId",
                table: "InboundMessageReceipt",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HistoricalImportRequestedUnixMilliseconds",
                table: "InboundMessageReceipt",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselineCompletedUnixMilliseconds",
                table: "MailboxIngestionState");

            migrationBuilder.DropColumn(
                name: "HistoricalImportCompletedUnixMilliseconds",
                table: "InboundMessageReceipt");

            migrationBuilder.DropColumn(
                name: "HistoricalImportErrorCode",
                table: "InboundMessageReceipt");

            migrationBuilder.DropColumn(
                name: "HistoricalImportRequestId",
                table: "InboundMessageReceipt");

            migrationBuilder.DropColumn(
                name: "HistoricalImportRequestedUnixMilliseconds",
                table: "InboundMessageReceipt");
        }
    }
}
