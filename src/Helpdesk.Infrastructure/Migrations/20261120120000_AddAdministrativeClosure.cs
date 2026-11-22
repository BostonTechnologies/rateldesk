using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261120120000_AddAdministrativeClosure")]
public sealed class AddAdministrativeClosure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "AdministrativeClosureReason", table: "Tickets", type: "text", nullable: true);
        migrationBuilder.AddColumn<string>(name: "AdministrativelyClosedBy", table: "Tickets", type: "text", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "AdministrativeClosureReason", table: "Tickets");
        migrationBuilder.DropColumn(name: "AdministrativelyClosedBy", table: "Tickets");
    }
}
