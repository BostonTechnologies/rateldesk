using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTicketQueueAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedSupportGroupId",
                table: "Tickets",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QueueAssignmentVersion",
                table: "Tickets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "QueueTransitionId",
                table: "SupportNotificationDeliveries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TicketAccessScope",
                table: "Roles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "AssignedSupportGroupId",
                table: "IncidentCreateReceipts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IncidentReceiverQueueRoutes",
                columns: table => new
                {
                    SourceNamespaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupportGroupId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentReceiverQueueRoutes", x => new { x.SourceNamespaceId, x.SupportGroupId });
                    table.ForeignKey(
                        name: "FK_IncidentReceiverQueueRoutes_IncidentReceiverSources_SourceN~",
                        column: x => x.SourceNamespaceId,
                        principalTable: "IncidentReceiverSources",
                        principalColumn: "SourceNamespaceId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_IncidentReceiverQueueRoutes_SupportGroups_SupportGroupId",
                        column: x => x.SupportGroupId,
                        principalTable: "SupportGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                name: "TicketQueueTransitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TicketId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TicketType = table.Column<int>(type: "integer", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AssignmentVersion = table.Column<long>(type: "bigint", nullable: false),
                    PreviousSupportGroupId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AssignedSupportGroupId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousAssigneeId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    AssignedToId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketQueueTransitions", x => x.Id);
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
                name: "IX_Tickets_AssignedSupportGroupId",
                table: "Tickets",
                column: "AssignedSupportGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_OrganizationId_AssignedSupportGroupId_State",
                table: "Tickets",
                columns: new[] { "OrganizationId", "AssignedSupportGroupId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentReceiverQueueRoutes_SupportGroupId",
                table: "IncidentReceiverQueueRoutes",
                column: "SupportGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_RoleSupportGroups_SupportGroupId",
                table: "RoleSupportGroups",
                column: "SupportGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketQueueTransitions_TicketId_AssignmentVersion",
                table: "TicketQueueTransitions",
                columns: new[] { "TicketId", "AssignmentVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketWorkforceRestrictions_OrganizationId",
                table: "TicketWorkforceRestrictions",
                column: "OrganizationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_SupportGroups_AssignedSupportGroupId",
                table: "Tickets",
                column: "AssignedSupportGroupId",
                principalTable: "SupportGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_SupportGroups_AssignedSupportGroupId",
                table: "Tickets");

            migrationBuilder.DropTable(
                name: "IncidentReceiverQueueRoutes");

            migrationBuilder.DropTable(
                name: "RoleSupportGroups");

            migrationBuilder.DropTable(
                name: "TicketQueueTransitions");

            migrationBuilder.DropTable(
                name: "TicketWorkforceRestrictions");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_AssignedSupportGroupId",
                table: "Tickets");

            migrationBuilder.DropIndex(
                name: "IX_Tickets_OrganizationId_AssignedSupportGroupId_State",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AssignedSupportGroupId",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QueueAssignmentVersion",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "QueueTransitionId",
                table: "SupportNotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "TicketAccessScope",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "AssignedSupportGroupId",
                table: "IncidentCreateReceipts");
        }
    }
}
