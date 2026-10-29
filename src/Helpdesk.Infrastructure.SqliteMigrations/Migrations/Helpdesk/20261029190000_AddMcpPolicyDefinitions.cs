using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk;

public partial class AddMcpPolicyDefinitions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "DefinitionJson", table: "McpAccessPolicy", type: "TEXT", nullable: false, defaultValue: "");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "DefinitionJson", table: "McpAccessPolicy");
}
