using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
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
                    MailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Transport = table.Column<int>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SmtpHost = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    SmtpPort = table.Column<int>(type: "INTEGER", nullable: false),
                    SmtpTlsMode = table.Column<int>(type: "INTEGER", nullable: false),
                    SmtpUsername = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ProtectedSmtpPassword = table.Column<string>(type: "TEXT", nullable: false),
                    LastTestUnixMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    TestedVersion = table.Column<long>(type: "INTEGER", nullable: true),
                    LastTestCode = table.Column<string>(type: "TEXT", nullable: true)
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
