using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261111163000_BindSelfServiceCustomerIdentity")]
public sealed class BindSelfServiceCustomerIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("IdentityUserId", "Customers", type: "character varying(128)", maxLength: 128, nullable: true);
        migrationBuilder.DropIndex("IX_Customers_Email", "Customers");
        migrationBuilder.CreateIndex("IX_Customers_Email", "Customers", "Email", unique: true, filter: "\"IdentityUserId\" IS NULL");
        migrationBuilder.CreateIndex("IX_Customers_IdentityUserId", "Customers", "IdentityUserId", unique: true, filter: "\"IdentityUserId\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Independent identity contacts can legitimately share an email. Refuse
        // a lossy downgrade; forward migrations preserve those requester IDs.
        throw new NotSupportedException("Identity-bound requester contacts require a database backup for downgrade.");
    }
}
