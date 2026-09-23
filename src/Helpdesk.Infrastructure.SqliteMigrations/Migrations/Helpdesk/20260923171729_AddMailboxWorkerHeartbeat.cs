using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class AddMailboxWorkerHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastHeartbeatUnixMilliseconds",
                table: "MailboxWorkerControl",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentStage",
                table: "MailboxIngestionState",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "LastAttemptUnixMilliseconds",
                table: "MailboxIngestionState",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastHeartbeatUnixMilliseconds",
                table: "MailboxWorkerControl");

            migrationBuilder.DropColumn(
                name: "CurrentStage",
                table: "MailboxIngestionState");

            migrationBuilder.DropColumn(
                name: "LastAttemptUnixMilliseconds",
                table: "MailboxIngestionState");
        }
    }
}
