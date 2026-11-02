using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class AddOrganizationChatScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_TicketId",
                table: "AiAssistantChatConversations");

            migrationBuilder.AlterColumn<string>(
                name: "TicketType",
                table: "AiAssistantChatConversations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "TicketId",
                table: "AiAssistantChatConversations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "CreationFingerprint",
                table: "AiAssistantChatConversations",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GuidanceSnapshot",
                table: "AiAssistantChatConversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveryLastAttemptUtc",
                table: "AiAssistantChatConversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryResult",
                table: "AiAssistantChatConversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Scope",
                table: "AiAssistantChatConversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "AiAssistantChatConversations",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_Scope_LastActivityUtc",
                table: "AiAssistantChatConversations",
                columns: new[] { "OrganizationId", "Scope", "LastActivityUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_TicketId",
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
            migrationBuilder.Sql("CREATE TEMP TABLE ChatScopeDowngradeGuard (Count INTEGER CHECK (Count = 0)); INSERT INTO ChatScopeDowngradeGuard SELECT COUNT(*) FROM AiAssistantChatConversations WHERE Scope = 1; DROP TABLE ChatScopeDowngradeGuard;");

            migrationBuilder.DropIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_Scope_LastActivityUtc",
                table: "AiAssistantChatConversations");

            migrationBuilder.DropIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_TicketId",
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
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "TicketId",
                table: "AiAssistantChatConversations",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiAssistantChatConversations_OrganizationId_TicketType_TicketId",
                table: "AiAssistantChatConversations",
                columns: new[] { "OrganizationId", "TicketType", "TicketId" },
                unique: true,
                filter: "\"State\" <> 4 AND \"IsInvestigation\" = FALSE");
        }
    }
}
