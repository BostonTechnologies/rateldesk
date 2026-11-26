using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderExecutionDiagnostic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderDiagnostic",
                table: "Tickets",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderDiagnostic",
                table: "Tickets");
        }
    }
}
