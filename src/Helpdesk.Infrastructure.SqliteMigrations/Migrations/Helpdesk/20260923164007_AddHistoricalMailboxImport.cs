using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
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
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HistoricalImportCompletedUnixMilliseconds",
                table: "InboundMessageReceipt",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HistoricalImportErrorCode",
                table: "InboundMessageReceipt",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "HistoricalImportRequestId",
                table: "InboundMessageReceipt",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HistoricalImportRequestedUnixMilliseconds",
                table: "InboundMessageReceipt",
                type: "INTEGER",
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
