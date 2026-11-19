using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCodexConnectionsAndSkillUploads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProtectedCodexToken",
                table: "OpenAiChatSessionGrant",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "SkillArchive",
                table: "OpenAiChatProfileRevision",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CodexConnectionId",
                table: "OpenAiChatProfile",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CodexConnection",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    OrganizationId = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AccountLabel = table.Column<string>(type: "text", nullable: true),
                    Plan = table.Column<string>(type: "text", nullable: true),
                    WorkspaceLabel = table.Column<string>(type: "text", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodexConnection", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpenAiChatProfile_CodexConnectionId",
                table: "OpenAiChatProfile",
                column: "CodexConnectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CodexConnection_OwnerId_OrganizationId_State",
                table: "CodexConnection",
                columns: new[] { "OwnerId", "OrganizationId", "State" });

            migrationBuilder.AddForeignKey(
                name: "FK_OpenAiChatProfile_CodexConnection_CodexConnectionId",
                table: "OpenAiChatProfile",
                column: "CodexConnectionId",
                principalTable: "CodexConnection",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OpenAiChatProfile_CodexConnection_CodexConnectionId",
                table: "OpenAiChatProfile");

            migrationBuilder.DropTable(
                name: "CodexConnection");

            migrationBuilder.DropIndex(
                name: "IX_OpenAiChatProfile_CodexConnectionId",
                table: "OpenAiChatProfile");

            migrationBuilder.DropColumn(
                name: "ProtectedCodexToken",
                table: "OpenAiChatSessionGrant");

            migrationBuilder.DropColumn(
                name: "SkillArchive",
                table: "OpenAiChatProfileRevision");

            migrationBuilder.DropColumn(
                name: "CodexConnectionId",
                table: "OpenAiChatProfile");
        }
    }
}
