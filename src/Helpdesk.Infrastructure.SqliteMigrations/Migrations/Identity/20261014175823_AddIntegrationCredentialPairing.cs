using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AddIntegrationCredentialPairing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntegrationCredentialPairingRateLimits",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WindowEndsAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCredentialPairingRateLimits", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationCredentialPairings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    HumanHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    McpResourceUri = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExpiresAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveryExpiresAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    LastPollAtUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: true),
                    ProtectedApproval = table.Column<string>(type: "TEXT", nullable: true),
                    CredentialId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProtectedDelivery = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCredentialPairings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentialPairingRateLimits_WindowEndsAtUnixMilliseconds",
                table: "IntegrationCredentialPairingRateLimits",
                column: "WindowEndsAtUnixMilliseconds");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentialPairings_DeviceHash",
                table: "IntegrationCredentialPairings",
                column: "DeviceHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentialPairings_ExpiresAtUnixMilliseconds",
                table: "IntegrationCredentialPairings",
                column: "ExpiresAtUnixMilliseconds");

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentialPairings_HumanHash",
                table: "IntegrationCredentialPairings",
                column: "HumanHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntegrationCredentialPairingRateLimits");

            migrationBuilder.DropTable(
                name: "IntegrationCredentialPairings");
        }
    }
}
