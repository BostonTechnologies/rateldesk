using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketWorkforceScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TicketAccessScope",
                table: "Roles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RoleSupportGroups",
                columns: table => new
                {
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    SupportGroupId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoleSupportGroups", x => new { x.RoleId, x.SupportGroupId });
                    table.ForeignKey(
                        name: "FK_RoleSupportGroups_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoleSupportGroups_SupportGroups_SupportGroupId",
                        column: x => x.SupportGroupId,
                        principalTable: "SupportGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketWorkforceRestrictions",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RestrictedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketWorkforceRestrictions", x => new { x.UserId, x.OrganizationId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_RoleSupportGroups_SupportGroupId",
                table: "RoleSupportGroups",
                column: "SupportGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketWorkforceRestrictions_OrganizationId",
                table: "TicketWorkforceRestrictions",
                column: "OrganizationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RoleSupportGroups");

            migrationBuilder.DropTable(
                name: "TicketWorkforceRestrictions");

            migrationBuilder.DropColumn(
                name: "TicketAccessScope",
                table: "Roles");
        }
    }
}
