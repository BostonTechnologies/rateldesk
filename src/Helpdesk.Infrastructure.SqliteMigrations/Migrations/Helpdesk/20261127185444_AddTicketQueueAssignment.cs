using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
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
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QueueAssignmentVersion",
                table: "Tickets",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "TicketQueueTransitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TicketId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TicketType = table.Column<int>(type: "INTEGER", nullable: false),
                    OrganizationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AssignmentVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    PreviousSupportGroupId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AssignedSupportGroupId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PreviousAssigneeId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    AssignedToId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    OccurredUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketQueueTransitions", x => x.Id);
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
                name: "IX_TicketQueueTransitions_TicketId_AssignmentVersion",
                table: "TicketQueueTransitions",
                columns: new[] { "TicketId", "AssignmentVersion" },
                unique: true);

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
                name: "TicketQueueTransitions");

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
        }
    }
}
