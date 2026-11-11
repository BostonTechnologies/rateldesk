using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261111160000_PersistSelfServiceSubmissions")]
public sealed class PersistSelfServiceSubmissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("SelfServiceSubmissionId", "Tickets", type: "TEXT", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<string>("SelfServiceSubmitterId", "Tickets", type: "TEXT", maxLength: 128, nullable: true);
        migrationBuilder.AddColumn<string>("SelfServiceSubmissionHash", "Tickets", type: "TEXT", maxLength: 64, nullable: true);
        migrationBuilder.CreateIndex("IX_Tickets_SelfServiceSubmitterId_SelfServiceSubmissionId", "Tickets",
            new[] { "SelfServiceSubmitterId", "SelfServiceSubmissionId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_Tickets_SelfServiceSubmitterId_SelfServiceSubmissionId", "Tickets");
        migrationBuilder.DropColumn("SelfServiceSubmissionId", "Tickets");
        migrationBuilder.DropColumn("SelfServiceSubmitterId", "Tickets");
        migrationBuilder.DropColumn("SelfServiceSubmissionHash", "Tickets");
    }
}
