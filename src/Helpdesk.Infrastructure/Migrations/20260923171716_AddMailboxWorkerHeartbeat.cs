using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
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
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentStage",
                table: "MailboxIngestionState",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "LastAttemptUnixMilliseconds",
                table: "MailboxIngestionState",
                type: "bigint",
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
