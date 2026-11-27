using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class AddSupportQueueRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "QueueTransitionId",
                table: "SupportNotificationDeliveries",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedSupportGroupId",
                table: "IncidentCreateReceipts",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IncidentReceiverQueueRoutes",
                columns: table => new
                {
                    SourceNamespaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SupportGroupId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentReceiverQueueRoutes", x => new { x.SourceNamespaceId, x.SupportGroupId });
                    table.ForeignKey(
                        name: "FK_IncidentReceiverQueueRoutes_IncidentReceiverSources_SourceNamespaceId",
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

            migrationBuilder.CreateIndex(
                name: "IX_IncidentReceiverQueueRoutes_SupportGroupId",
                table: "IncidentReceiverQueueRoutes",
                column: "SupportGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncidentReceiverQueueRoutes");

            migrationBuilder.DropColumn(
                name: "QueueTransitionId",
                table: "SupportNotificationDeliveries");

            migrationBuilder.DropColumn(
                name: "AssignedSupportGroupId",
                table: "IncidentCreateReceipts");
        }
    }
}
