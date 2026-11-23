using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LaunchDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TaskId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Recipient = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: false),
                    Legacy = table.Column<bool>(type: "boolean", nullable: false),
                    BrowserDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    GrantExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                    RevokedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicLinkCapability", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublicLinkCompatibility",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LegacyCutoffUnix = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicLinkCompatibility", x => x.Id);
                });

            // Keep the existing mailed-link grace period across restarts and upgrades.
            migrationBuilder.Sql("""
                INSERT INTO "PublicLinkCompatibility" ("Id", "LegacyCutoffUnix")
                VALUES (1, CAST(FLOOR(EXTRACT(EPOCH FROM CURRENT_TIMESTAMP)) AS bigint) + 2592000);
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
