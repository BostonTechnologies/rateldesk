using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261015060000_IndexPendingEmailBrowse")]
public sealed class IndexPendingEmailBrowse : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE \"TicketTimelineEvents\" ADD COLUMN \"CreatedUtcSortTicks\" INTEGER GENERATED ALWAYS AS (CAST(strftime('%s', substr(\"CreatedUtc\", 1, 19) || substr(\"CreatedUtc\", -6)) AS INTEGER) * 10000000 + CAST(substr((CASE WHEN substr(\"CreatedUtc\", 20, 1) = '.' THEN substr(\"CreatedUtc\", 21, length(\"CreatedUtc\") - 26) ELSE '' END) || '0000000', 1, 7) AS INTEGER) + 621355968000000000) VIRTUAL");
        migrationBuilder.CreateIndex("IX_TicketTimelineEvents_EventType_EmailStatus_CreatedUtcSortTicks_Id",
            "TicketTimelineEvents", ["EventType", "EmailStatus", "CreatedUtcSortTicks", "Id"]);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_TicketTimelineEvents_EventType_EmailStatus_CreatedUtcSortTicks_Id", "TicketTimelineEvents");
        migrationBuilder.Sql("ALTER TABLE \"TicketTimelineEvents\" DROP COLUMN \"CreatedUtcSortTicks\"");
    }
}
