using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Identity.Migrations
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
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WindowEndsAtUnixMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCredentialPairingRateLimits", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationCredentialPairings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HumanHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    McpResourceUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpiresAtUnixMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryExpiresAtUnixMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    LastPollAtUnixMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ProtectedApproval = table.Column<string>(type: "text", nullable: true),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProtectedDelivery = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationCredentialPairings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationCredentialPairingRateLimits_WindowEndsAtUnixMill~",
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
