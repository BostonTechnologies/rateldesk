using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class ScopeManagedRequestersByOrganization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_IdentityUserId",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_IdentityUserId_OrganizationId",
                table: "Customers",
                columns: new[] { "IdentityUserId", "OrganizationId" },
                unique: true,
                filter: "\"IdentityUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_IdentityUserId_OrganizationId",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_IdentityUserId",
                table: "Customers",
                column: "IdentityUserId",
                unique: true,
                filter: "\"IdentityUserId\" IS NOT NULL");
        }
    }
}
