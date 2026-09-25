using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class AddMailboxSyncCommands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastSyncCommandErrorCode",
                table: "MailboxIngestionState",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastSyncCommandUnixMilliseconds",
                table: "MailboxIngestionState",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SyncCompletedVersion",
                table: "MailboxIngestionState",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "SyncRequestedVersion",
                table: "MailboxIngestionState",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSyncCommandErrorCode",
                table: "MailboxIngestionState");

            migrationBuilder.DropColumn(
                name: "LastSyncCommandUnixMilliseconds",
                table: "MailboxIngestionState");

            migrationBuilder.DropColumn(
                name: "SyncCompletedVersion",
                table: "MailboxIngestionState");

            migrationBuilder.DropColumn(
                name: "SyncRequestedVersion",
                table: "MailboxIngestionState");
        }
    }
}
