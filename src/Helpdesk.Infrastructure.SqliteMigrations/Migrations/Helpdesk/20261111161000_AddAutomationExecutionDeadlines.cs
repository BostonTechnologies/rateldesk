using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261111161000_AddAutomationExecutionDeadlines")]
public sealed class AddAutomationExecutionDeadlines : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "WorkflowStateVersion", table: "Tickets", type: "INTEGER", nullable: true, defaultValue: 0L);
        migrationBuilder.AddColumn<long>(name: "AutomationDeadlineUtcTicks", table: "Tickets", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<long>(name: "AutomationNextCheckUtcTicks", table: "Tickets", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "AutomationTimedOutAt", table: "Tickets", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "AutomationTimedOutAttemptId", table: "Tickets", type: "TEXT", nullable: true);
        migrationBuilder.AddColumn<string>(name: "AutomationReconciliationLeaseId", table: "Tickets", type: "TEXT", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<long>(name: "AutomationReconciliationLeaseUntilUtcTicks", table: "Tickets", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "AutomationContinuationPending", table: "Tickets", type: "INTEGER", nullable: true, defaultValue: false);
        migrationBuilder.AddColumn<long>(name: "AutomationStateVersion", table: "Tickets", type: "INTEGER", nullable: true, defaultValue: 0L);
        migrationBuilder.CreateIndex(name: "IX_Tickets_Type_AutomationNextCheckUtcTicks", table: "Tickets", columns: ["Type", "AutomationNextCheckUtcTicks"]);
        migrationBuilder.CreateIndex(name: "IX_Tickets_Type_AutomationContinuationPending", table: "Tickets", columns: ["Type", "AutomationContinuationPending"]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Tickets_Type_AutomationNextCheckUtcTicks", table: "Tickets");
        migrationBuilder.DropIndex(name: "IX_Tickets_Type_AutomationContinuationPending", table: "Tickets");
        migrationBuilder.DropColumn(name: "WorkflowStateVersion", table: "Tickets");
        migrationBuilder.DropColumn(name: "AutomationDeadlineUtcTicks", table: "Tickets");
        migrationBuilder.DropColumn(name: "AutomationNextCheckUtcTicks", table: "Tickets");
        migrationBuilder.DropColumn(name: "AutomationTimedOutAt", table: "Tickets");
        migrationBuilder.DropColumn(name: "AutomationTimedOutAttemptId", table: "Tickets");
        migrationBuilder.DropColumn(name: "AutomationReconciliationLeaseId", table: "Tickets");
        migrationBuilder.DropColumn(name: "AutomationReconciliationLeaseUntilUtcTicks", table: "Tickets");
        migrationBuilder.DropColumn(name: "AutomationContinuationPending", table: "Tickets");
        migrationBuilder.DropColumn(name: "AutomationStateVersion", table: "Tickets");
    }
}

