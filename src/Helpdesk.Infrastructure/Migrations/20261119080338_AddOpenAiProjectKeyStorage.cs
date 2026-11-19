using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOpenAiProjectKeyStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WebKeyId",
                table: "OpenAiChatProfileRevision",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedWebKey",
                table: "OpenAiChatProfile",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WebKeyId",
                table: "OpenAiChatProfile",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WebKeyId",
                table: "OpenAiChatProfileRevision");

            migrationBuilder.DropColumn(
                name: "ProtectedWebKey",
                table: "OpenAiChatProfile");

            migrationBuilder.DropColumn(
                name: "WebKeyId",
                table: "OpenAiChatProfile");
        }
    }
}
