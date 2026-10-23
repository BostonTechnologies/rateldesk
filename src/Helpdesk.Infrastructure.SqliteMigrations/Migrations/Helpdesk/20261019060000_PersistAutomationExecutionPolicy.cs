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
            WITH policies AS (
                SELECT r."Id" AS request_id,
                    json_extract(CASE WHEN item.type = 'object' THEN item.value ELSE '{}' END, '$.id') AS template_id,
                    json_extract(CASE WHEN item.type = 'object' THEN item.value ELSE '{}' END, '$.autoStart') AS auto_start
                FROM "Tickets" r JOIN "RequestForms" f ON f."Id" = r."RequestFormId",
                    json_each(CASE WHEN json_type(f."JsonSchema", '$.tasks') = 'array'
                        THEN json_extract(f."JsonSchema", '$.tasks') ELSE '[]' END) item
                WHERE json_type(CASE WHEN item.type = 'object' THEN item.value ELSE '{}' END, '$.autoStart') IN ('true','false')
            )
            UPDATE "Tickets" AS t SET "AutoStart" = (
                SELECT p.auto_start FROM policies p
                WHERE p.request_id = t."RequestId" AND p.template_id = t."TemplateId" LIMIT 1)
            WHERE t."Discriminator" = 'RequestTask' AND EXISTS (
                SELECT 1 FROM policies p WHERE p.request_id = t."RequestId" AND p.template_id = t."TemplateId")
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AutoStart", "Tickets");
        migrationBuilder.DropColumn("AutomationAttemptCorrelationId", "Tickets");
        migrationBuilder.DropColumn("AutomationAttemptHistoryJson", "Tickets");
    }
}
