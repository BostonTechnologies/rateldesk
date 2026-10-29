using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Helpdesk.Infrastructure.Identity.Migrations;

public partial class AddIntegrationSetupAttempts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "IntegrationCredentialSetupAttempts",
            columns: table => new
            {
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                CredentialId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                DeliveryExpiresAtUnixMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                ProtectedDelivery = table.Column<string>(type: "text", nullable: true),
                Revision = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_IntegrationCredentialSetupAttempts", x => x.RequestId));
        migrationBuilder.CreateIndex(name: "IX_IntegrationCredentialSetupAttempts_CredentialId",
            table: "IntegrationCredentialSetupAttempts", column: "CredentialId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_IntegrationSetup_State_Deadline",
            table: "IntegrationCredentialSetupAttempts", columns: new[] { "State", "DeliveryExpiresAtUnixMilliseconds" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "IntegrationCredentialSetupAttempts");
}
