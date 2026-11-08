using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class AddPendingEmailOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingEmailOperationRecord",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingEmailOperationRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingEmailOperationTarget",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Snapshot = table.Column<string>(type: "TEXT", nullable: false),
                    Eligible = table.Column<bool>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingEmailOperationTarget", x => new { x.OperationId, x.DeliveryId });
                    table.ForeignKey(
                        name: "FK_PendingEmailOperationTarget_PendingEmailOperationRecord_OperationId",
                        column: x => x.OperationId,
                        principalTable: "PendingEmailOperationRecord",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingEmailOperationRecord_ActorId_CreatedUnixMilliseconds",
                table: "PendingEmailOperationRecord",
                columns: new[] { "ActorId", "CreatedUnixMilliseconds" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingEmailOperationTarget_OperationId_Sequence",
                table: "PendingEmailOperationTarget",
                columns: new[] { "OperationId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingEmailOperationTarget");

            migrationBuilder.DropTable(
                name: "PendingEmailOperationRecord");
        }
    }
}
