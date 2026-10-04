using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceNamespaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CustomerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrincipalKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentReceiverSourceAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IncidentReceiverSources",
                columns: table => new
                {
                    SourceNamespaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CustomerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentReceiverSources", x => x.SourceNamespaceId);
                });

            migrationBuilder.CreateTable(
                name: "IncidentCreateReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceNamespaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CustomerId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IncidentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Location = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AcceptedJson = table.Column<string>(type: "text", nullable: false),
                    CommittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentCreateReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentCreateReceipts_IncidentReceiverSources_SourceNamesp~",
                        column: x => x.SourceNamespaceId,
                        principalTable: "IncidentReceiverSources",
                        principalColumn: "SourceNamespaceId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IncidentReceiverPrincipalBindings",
                columns: table => new
                {
                    SourceNamespaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrincipalId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentReceiverPrincipalBindings", x => new { x.SourceNamespaceId, x.PrincipalKind, x.PrincipalId });
                    table.ForeignKey(
                        name: "FK_IncidentReceiverPrincipalBindings_IncidentReceiverSources_S~",
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
