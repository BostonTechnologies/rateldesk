using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class AddIncidentReceiver : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IncidentReceiverSourceAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceNamespaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OrganizationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CustomerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PrincipalKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentReceiverSourceAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IncidentReceiverSources",
                columns: table => new
                {
                    SourceNamespaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrganizationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CustomerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentReceiverSources", x => x.SourceNamespaceId);
                });

            migrationBuilder.CreateTable(
                name: "IncidentCreateReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceNamespaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OrganizationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CustomerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IncidentId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Location = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AcceptedJson = table.Column<string>(type: "TEXT", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentCreateReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentCreateReceipts_IncidentReceiverSources_SourceNamespaceId",
                        column: x => x.SourceNamespaceId,
                        principalTable: "IncidentReceiverSources",
                        principalColumn: "SourceNamespaceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncidentReceiverPrincipalBindings",
                columns: table => new
                {
                    SourceNamespaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentReceiverPrincipalBindings", x => new { x.SourceNamespaceId, x.PrincipalKind, x.PrincipalId });
                    table.ForeignKey(
                        name: "FK_IncidentReceiverPrincipalBindings_IncidentReceiverSources_SourceNamespaceId",
                        column: x => x.SourceNamespaceId,
                        principalTable: "IncidentReceiverSources",
                        principalColumn: "SourceNamespaceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentCreateReceipts_SourceNamespaceId_Key",
                table: "IncidentCreateReceipts",
                columns: new[] { "SourceNamespaceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentReceiverSourceAudits_SourceNamespaceId_Revision",
                table: "IncidentReceiverSourceAudits",
                columns: new[] { "SourceNamespaceId", "Revision" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentReceiverSources_SourceInstanceId",
                table: "IncidentReceiverSources",
                column: "SourceInstanceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncidentCreateReceipts");

            migrationBuilder.DropTable(
                name: "IncidentReceiverPrincipalBindings");

            migrationBuilder.DropTable(
                name: "IncidentReceiverSourceAudits");

            migrationBuilder.DropTable(
                name: "IncidentReceiverSources");
        }
    }
}
