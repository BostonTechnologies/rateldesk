using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261029181000_RemoveLegacyNetclawConversationIndex")]
public sealed class RemoveLegacyNetclawConversationIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Retained installations can still carry the original Netclaw index.
        // The replacement index owns normal-conversation uniqueness and excludes
        // investigations; this predecessor otherwise defeats that exemption.
        migrationBuilder.Sql("""
            DROP INDEX IF EXISTS "IX_NetclawChatConversations_OrganizationId_TicketType_TicketId";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not restore a redundant constraint that rejects valid investigations.
        // The replacement filtered index remains responsible for normal chats.
    }
}
