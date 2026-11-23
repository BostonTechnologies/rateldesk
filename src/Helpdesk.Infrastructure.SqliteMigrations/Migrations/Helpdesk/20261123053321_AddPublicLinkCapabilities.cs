using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class AddPublicLinkCapabilities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicLinkCapability",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LaunchDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Purpose = table.Column<int>(type: "INTEGER", nullable: false),
                    TicketId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OrganizationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TaskId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Recipient = table.Column<string>(type: "TEXT", maxLength: 254, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Legacy = table.Column<bool>(type: "INTEGER", nullable: false),
                    BrowserDigest = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    GrantExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicLinkCapability", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicLinkCompatibility",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LegacyCutoffUnix = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicLinkCompatibility", x => x.Id);
                });

            // Keep the existing mailed-link grace period across restarts and upgrades.
            migrationBuilder.Sql("""
                INSERT INTO "PublicLinkCompatibility" ("Id", "LegacyCutoffUnix")
                VALUES (1, unixepoch('now') + 2592000);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PublicLinkCapability_ExpiresAt",
                table: "PublicLinkCapability",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PublicLinkCapability_LaunchDigest",
                table: "PublicLinkCapability",
                column: "LaunchDigest",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicLinkCapability");

            migrationBuilder.DropTable(
                name: "PublicLinkCompatibility");
        }
    }
}
