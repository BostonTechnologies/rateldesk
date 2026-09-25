using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxOutgoingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailboxOutgoingSettings",
                columns: table => new
                {
                    MailboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Transport = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SmtpHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpTlsMode = table.Column<int>(type: "integer", nullable: false),
                    SmtpUsername = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ProtectedSmtpPassword = table.Column<string>(type: "text", nullable: false),
                    LastTestUnixMilliseconds = table.Column<long>(type: "bigint", nullable: true),
                    TestedVersion = table.Column<long>(type: "bigint", nullable: true),
                    LastTestCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailboxOutgoingSettings", x => x.MailboxId);
                    table.ForeignKey(
                        name: "FK_MailboxOutgoingSettings_EmailInboxSettings_MailboxId",
                        column: x => x.MailboxId,
                        principalTable: "EmailInboxSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailboxOutgoingSettings");
        }
    }
}
