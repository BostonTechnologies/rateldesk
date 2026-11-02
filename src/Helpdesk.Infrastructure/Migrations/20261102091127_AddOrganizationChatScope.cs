using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationChatScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_Tick~",
                table: "AiAssistantChatConversations");

            migrationBuilder.AlterColumn<string>(
                name: "TicketType",
                table: "AiAssistantChatConversations",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "TicketId",
                table: "AiAssistantChatConversations",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "CreationFingerprint",
                table: "AiAssistantChatConversations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GuidanceSnapshot",
                table: "AiAssistantChatConversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveryLastAttemptUtc",
                table: "AiAssistantChatConversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryResult",
                table: "AiAssistantChatConversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "AiAssistantChatConversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "AiAssistantChatConversations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_Scope_LastActiv~",
                table: "AiAssistantChatConversations",
                columns: new[] { "OrganizationId", "Scope", "LastActivityUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_Tick~",
                table: "AiAssistantChatConversations",
                columns: new[] { "OrganizationId", "TicketType", "TicketId" },
                unique: true,
                filter: "\"State\" <> 4 AND \"IsInvestigation\" = FALSE AND \"Scope\" = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ChatConversation_Scope",
                table: "AiAssistantChatConversations",
                sql: "(\"Scope\" = 0 AND \"TicketId\" IS NOT NULL AND \"TicketType\" IS NOT NULL) OR (\"Scope\" = 1 AND \"TicketId\" IS NULL AND \"TicketType\" IS NULL AND \"IsInvestigation\" = FALSE)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // General transcripts cannot be represented by the old ticket-only schema.
            migrationBuilder.Sql("DO $$ BEGIN IF EXISTS (SELECT 1 FROM \"AiAssistantChatConversations\" WHERE \"Scope\" = 1) THEN RAISE EXCEPTION 'Cannot downgrade while organization chat transcripts exist'; END IF; END $$;");

            migrationBuilder.DropIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_Scope_LastActiv~",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_Tick~",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ChatConversation_Scope",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "CreationFingerprint",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "GuidanceSnapshot",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "RecoveryLastAttemptUtc",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "RecoveryResult",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "Scope",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "AiAssistantChatConversations");

            migrationBuilder.AlterColumn<string>(
                name: "TicketType",
                table: "AiAssistantChatConversations",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TicketId",
                table: "AiAssistantChatConversations",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_Tick~",
                table: "AiAssistantChatConversations",
                columns: new[] { "OrganizationId", "TicketType", "TicketId" },
                unique: true,
                filter: "\"State\" <> 4 AND \"IsInvestigation\" = FALSE");
        }
    }
}
