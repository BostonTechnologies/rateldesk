using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261111170000_TrackRequestOperatorHolds")]
public sealed class TrackRequestOperatorHolds : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "OperatorHold", table: "Tickets", type: "boolean", nullable: true);
        // Older operator routes recorded only OnHold. Keep such ambiguous
        // historical holds; the canonical reconciliation hold has its own owner.
        migrationBuilder.Sql("""
            UPDATE "Tickets" SET "OperatorHold" = CASE
                WHEN "State" = 5 AND COALESCE("WorkflowStatus", '') NOT IN ('Blocked', 'Failed', 'Cancelled')
                    AND NOT (COALESCE("WorkflowStatus", '') = 'NeedsReconciliation'
                        AND COALESCE("WorkflowBlockReason", '') = 'Automation outcome is unknown. Reconcile the recorded attempt before continuing.')
                THEN TRUE ELSE FALSE END WHERE "Discriminator" = 'Request';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropColumn(name: "OperatorHold", table: "Tickets");
}
