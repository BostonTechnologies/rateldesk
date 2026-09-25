using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FairMailboxOutboxDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DispatchGroup",
                table: "MailboxOutboxEffect",
                type: "character varying(80)",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE "MailboxOutboxEffect"
                SET "DispatchGroup" = 'legacy:' || "Id"::text
                WHERE "DispatchGroup" = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_MailboxOutboxEffect_Kind_State_DispatchGroup_AvailableUnixM~",
                table: "MailboxOutboxEffect",
                columns: new[] { "Kind", "State", "DispatchGroup", "AvailableUnixMilliseconds" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MailboxOutboxEffect_Kind_State_DispatchGroup_AvailableUnixM~",
                table: "MailboxOutboxEffect");

            migrationBuilder.DropColumn(
                name: "DispatchGroup",
                table: "MailboxOutboxEffect");
        }
    }
}
