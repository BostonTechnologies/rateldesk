using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
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
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkflowFailureCategory",
                table: "Tickets",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowFailureTaskId",
                table: "Tickets",
                type: "TEXT",
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
