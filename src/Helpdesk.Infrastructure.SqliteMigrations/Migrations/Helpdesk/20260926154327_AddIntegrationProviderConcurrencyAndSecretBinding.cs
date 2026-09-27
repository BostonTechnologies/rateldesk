using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class AddIntegrationProviderConcurrencyAndSecretBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProfileFingerprint",
                table: "NetclawConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "NetclawConnectivitySettings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SecretBindingFingerprint",
                table: "NetclawConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecretBindingRevision",
                table: "NetclawConnectivitySettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowPrivateHttp",
                table: "M2MConnectivitySettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProfileFingerprint",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SecretBindingFingerprint",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecretBindingRevision",
                table: "M2MConnectivitySettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IntegrationProviderSettingsDuplicateArchives",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderTable = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OriginalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtectedClientSecret = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedDeviceToken = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    SecretBindingFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    SecretBindingRevision = table.Column<int>(type: "INTEGER", nullable: true),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationProviderSettingsDuplicateArchives", x => x.Id);
                });

            migrationBuilder.Sql("UPDATE \"M2MConnectivitySettings\" SET \"ProviderKey\" = 'Orchestrator' WHERE \"ProviderKey\" = '';");
            migrationBuilder.Sql("UPDATE \"NetclawConnectivitySettings\" SET \"ProviderKey\" = 'Netclaw' WHERE \"ProviderKey\" = '';");
            migrationBuilder.Sql("""
                INSERT INTO "IntegrationProviderSettingsDuplicateArchives"
                    ("ProviderTable", "OriginalId", "ProviderKey", "Revision", "ProtectedClientSecret", "ProfileFingerprint", "SecretBindingFingerprint", "SecretBindingRevision", "ArchivedAtUtc")
                SELECT 'M2MConnectivitySettings', "Id", "ProviderKey", "Revision", "ProtectedClientSecret", "ProfileFingerprint", "SecretBindingFingerprint", "SecretBindingRevision", CURRENT_TIMESTAMP
                FROM (
                    SELECT "Id", "ProviderKey", "Revision", "ProtectedClientSecret", "ProfileFingerprint", "SecretBindingFingerprint", "SecretBindingRevision",
                        ROW_NUMBER() OVER (PARTITION BY "ProviderKey" ORDER BY "Revision" DESC, "Id") AS rn
                    FROM "M2MConnectivitySettings"
                ) ranked
                WHERE rn > 1;
                DELETE FROM "M2MConnectivitySettings"
                WHERE "Id" IN (
                    SELECT "Id" FROM (
                        SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "ProviderKey" ORDER BY "Revision" DESC, "Id") AS rn
                        FROM "M2MConnectivitySettings"
                    ) ranked WHERE rn > 1
                );
                """);
            migrationBuilder.Sql("""
                INSERT INTO "IntegrationProviderSettingsDuplicateArchives"
                    ("ProviderTable", "OriginalId", "ProviderKey", "Revision", "ProtectedDeviceToken", "ProfileFingerprint", "SecretBindingFingerprint", "SecretBindingRevision", "ArchivedAtUtc")
                SELECT 'NetclawConnectivitySettings', "Id", "ProviderKey", "Revision", "ProtectedDeviceToken", "ProfileFingerprint", "SecretBindingFingerprint", "SecretBindingRevision", CURRENT_TIMESTAMP
                FROM (
                    SELECT "Id", "ProviderKey", "Revision", "ProtectedDeviceToken", "ProfileFingerprint", "SecretBindingFingerprint", "SecretBindingRevision",
                        ROW_NUMBER() OVER (PARTITION BY "ProviderKey" ORDER BY "Revision" DESC, "Id") AS rn
                    FROM "NetclawConnectivitySettings"
                ) ranked
                WHERE rn > 1;
                DELETE FROM "NetclawConnectivitySettings"
                WHERE "Id" IN (
                    SELECT "Id" FROM (
                        SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "ProviderKey" ORDER BY "Revision" DESC, "Id") AS rn
                        FROM "NetclawConnectivitySettings"
                    ) ranked WHERE rn > 1
                );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_NetclawConnectivitySettings_ProviderKey",
                table: "NetclawConnectivitySettings",
                column: "ProviderKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_M2MConnectivitySettings_ProviderKey",
                table: "M2MConnectivitySettings",
                column: "ProviderKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_NetclawConnectivitySettings_ProviderKey",
                table: "NetclawConnectivitySettings");

            migrationBuilder.DropIndex(
                name: "IX_M2MConnectivitySettings_ProviderKey",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropTable(
                name: "IntegrationProviderSettingsDuplicateArchives");

            migrationBuilder.DropColumn(
                name: "ProfileFingerprint",
                table: "NetclawConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "ProviderKey",
                table: "NetclawConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "SecretBindingFingerprint",
                table: "NetclawConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "SecretBindingRevision",
                table: "NetclawConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "AllowPrivateHttp",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "ProfileFingerprint",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "ProviderKey",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "SecretBindingFingerprint",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "SecretBindingRevision",
                table: "M2MConnectivitySettings");
        }
    }
}
