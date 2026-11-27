using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OwnRequestFailureHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkflowFailureAttemptId",
                table: "Tickets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkflowFailureCategory",
                table: "Tickets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowFailureTaskId",
                table: "Tickets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkflowFailureAttemptId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "WorkflowFailureCategory",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "WorkflowFailureTaskId",
                table: "Tickets");
        }
    }
}
