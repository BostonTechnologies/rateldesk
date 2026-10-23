using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261019060000_PersistAutomationExecutionPolicy")]
public sealed class PersistAutomationExecutionPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "AutoStart", table: "Tickets", type: "boolean", nullable: true, defaultValue: true);
        migrationBuilder.AddColumn<string>(name: "AutomationAttemptCorrelationId", table: "Tickets", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "AutomationAttemptHistoryJson", table: "Tickets", type: "text", nullable: true);
        migrationBuilder.Sql("""
            UPDATE "Tickets" t SET "AutoStart" = (item.value->>'autoStart')::boolean
            FROM "Tickets" r JOIN "RequestForms" f ON f."Id" = r."RequestFormId"
            CROSS JOIN LATERAL jsonb_array_elements(
                CASE WHEN jsonb_typeof(f."JsonSchema"->'tasks') = 'array'
                THEN f."JsonSchema"->'tasks' ELSE '[]'::jsonb END) item
            WHERE t."Discriminator" = 'RequestTask' AND t."RequestId" = r."Id"
                AND t."TemplateId" = item.value->>'id' AND jsonb_typeof(item.value->'autoStart') = 'boolean'
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("AutoStart", "Tickets");
        migrationBuilder.DropColumn("AutomationAttemptCorrelationId", "Tickets");
        migrationBuilder.DropColumn("AutomationAttemptHistoryJson", "Tickets");
    }
}
