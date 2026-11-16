using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutomationDispatchPayloadJson",
                table: "Tickets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutomationOriginJson",
                table: "Tickets",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutomationIncidentReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceRequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Html = table.Column<string>(type: "text", nullable: false),
                    PlainText = table.Column<string>(type: "text", nullable: false),
                    ContextJson = table.Column<string>(type: "text", nullable: false),
                    PresentationJson = table.Column<string>(type: "text", nullable: false),
                    FlowRunId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ActionId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationIncidentReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomationIncidentReports_Tickets_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InternalIncidentTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectTemplate = table.Column<string>(type: "text", nullable: false),
                    HtmlTemplate = table.Column<string>(type: "text", nullable: false),
                    LayoutId = table.Column<int>(type: "integer", nullable: true),
                    SourceKindsJson = table.Column<string>(type: "text", nullable: false),
                    Retired = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedRevision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalIncidentTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InternalIncidentTemplateRevisions",
                columns: table => new
                {
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    OrganizationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SubjectTemplate = table.Column<string>(type: "text", nullable: false),
                    ExpandedHtml = table.Column<string>(type: "text", nullable: false),
                    SourceKindsJson = table.Column<string>(type: "text", nullable: false),
                    ContentDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LayoutDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InternalIncidentTemplateRevisions", x => new { x.TemplateId, x.Revision });
                    table.ForeignKey(
                        name: "FK_InternalIncidentTemplateRevisions_InternalIncidentTemplates~",
                        column: x => x.TemplateId,
                        principalTable: "InternalIncidentTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationIncidentReports_IncidentId",
                table: "AutomationIncidentReports",
                column: "IncidentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutomationIncidentReports_SourceRequestId",
                table: "AutomationIncidentReports",
                column: "SourceRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_InternalIncidentTemplates_OrganizationId_Retired",
                table: "InternalIncidentTemplates",
                columns: new[] { "OrganizationId", "Retired" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutomationIncidentReports");

            migrationBuilder.DropTable(
                name: "InternalIncidentTemplateRevisions");

            migrationBuilder.DropTable(
                name: "InternalIncidentTemplates");

            migrationBuilder.DropColumn(
                name: "AutomationDispatchPayloadJson",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "AutomationOriginJson",
                table: "Tickets");
        }
    }
}
