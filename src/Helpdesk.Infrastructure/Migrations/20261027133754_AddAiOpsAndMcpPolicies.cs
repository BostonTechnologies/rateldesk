using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiOpsAndMcpPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The original hand-authored migration used this long identifier.
            // PostgreSQL truncates it at 63 bytes. Retained databases also
            // contain EF's later tilde-suffixed snapshot convention; either
            // predecessor must be removed before installing the new filter.
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_AiAssistantChatConversations_OrganizationId_TicketType_TicketId";
                DROP INDEX IF EXISTS "IX_AiAssistantChatConversations_OrganizationId_TicketType_Tick~";
                """);

            migrationBuilder.AddColumn<bool>(
                name: "IsInvestigation",
                table: "AiAssistantChatConversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AiGuidancePrompt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Archived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiGuidancePrompt", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiOpsInvestigation",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    TicketId = table.Column<string>(type: "text", nullable: false),
                    TicketType = table.Column<string>(type: "text", nullable: false),
                    TicketIdsJson = table.Column<string>(type: "text", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "text", nullable: false),
                    ContextSnapshot = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiOpsInvestigation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiOpsInvestigation_AiAssistantChatConversations_Id",
                        column: x => x.Id,
                        principalTable: "AiAssistantChatConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "McpAccessPolicy",
                columns: table => new
                {
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<string>(type: "text", nullable: false),
                    PermissionsJson = table.Column<string>(type: "text", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpAccessPolicy", x => x.CredentialId);
                });

            migrationBuilder.CreateTable(
                name: "McpAccessPolicyAudit",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    OccurredUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpAccessPolicyAudit", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_Tick~",
                table: "AiAssistantChatConversations",
                columns: new[] { "OrganizationId", "TicketType", "TicketId" },
                unique: true,
                filter: "\"State\" <> 4 AND \"IsInvestigation\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_AiGuidancePrompt_OrganizationId",
                table: "AiGuidancePrompt",
                column: "OrganizationId",
                unique: true,
                filter: "\"IsDefault\" = TRUE AND \"Archived\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_AiOpsInvestigation_OrganizationId_CreatedUtc",
                table: "AiOpsInvestigation",
                columns: new[] { "OrganizationId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_McpAccessPolicyAudit_CredentialId_Revision",
                table: "McpAccessPolicyAudit",
                columns: new[] { "CredentialId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiGuidancePrompt");

            migrationBuilder.DropTable(
                name: "AiOpsInvestigation");

            migrationBuilder.DropTable(
                name: "McpAccessPolicy");

            migrationBuilder.DropTable(
                name: "McpAccessPolicyAudit");

            migrationBuilder.DropIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_Tick~",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "IsInvestigation",
                table: "AiAssistantChatConversations");

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_TicketId",
                table: "AiAssistantChatConversations",
                columns: new[] { "OrganizationId", "TicketType", "TicketId" },
                unique: true,
                filter: "\"State\" <> 4");
        }
    }
}
