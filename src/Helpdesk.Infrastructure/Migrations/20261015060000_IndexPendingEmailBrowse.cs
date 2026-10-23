using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261015060000_IndexPendingEmailBrowse")]
public sealed class IndexPendingEmailBrowse : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex(
        "IX_TicketTimelineEvents_EventType_EmailStatus_CreatedUtc_Id", "TicketTimelineEvents",
        ["EventType", "EmailStatus", "CreatedUtc", "Id"]);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex(
        "IX_TicketTimelineEvents_EventType_EmailStatus_CreatedUtc_Id", "TicketTimelineEvents");
}
