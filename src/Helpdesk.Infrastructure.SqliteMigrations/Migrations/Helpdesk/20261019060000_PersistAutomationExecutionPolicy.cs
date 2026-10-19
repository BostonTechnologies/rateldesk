using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261019060000_PersistAutomationExecutionPolicy")]
public sealed class PersistAutomationExecutionPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "AutoStart", table: "Tickets", type: "INTEGER", nullable: true, defaultValue: true);
        migrationBuilder.AddColumn<string>(name: "AutomationAttemptCorrelationId", table: "Tickets", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "AutomationAttemptHistoryJson", table: "Tickets", type: "TEXT", nullable: true);
        migrationBuilder.Sql("""
            UPDATE "Tickets" AS t SET "AutoStart" = (SELECT json_extract(item.value, '$.autoStart') FROM "Tickets" r JOIN "RequestForms" f ON f."Id" = r."RequestFormId", json_each(f."JsonSchema", '$.tasks') item WHERE r."Id" = t."RequestId" AND json_extract(item.value, '$.id') = t."TemplateId" AND json_type(item.value, '$.autoStart') IN ('true','false') LIMIT 1) WHERE t."Discriminator" = 'RequestTask' AND EXISTS (SELECT 1 FROM "Tickets" r JOIN "RequestForms" f ON f."Id" = r."RequestFormId", json_each(f."JsonSchema", '$.tasks') item WHERE r."Id" = t."RequestId" AND json_extract(item.value, '$.id') = t."TemplateId" AND json_type(item.value, '$.autoStart') IN ('true','false'))
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AutoStart", "Tickets");
        migrationBuilder.DropColumn("AutomationAttemptCorrelationId", "Tickets");
        migrationBuilder.DropColumn("AutomationAttemptHistoryJson", "Tickets");
    }
}
