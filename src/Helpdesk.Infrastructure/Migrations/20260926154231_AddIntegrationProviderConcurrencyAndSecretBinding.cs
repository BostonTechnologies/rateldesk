using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
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
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "NetclawConnectivitySettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SecretBindingFingerprint",
                table: "NetclawConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecretBindingRevision",
                table: "NetclawConnectivitySettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowPrivateHttp",
                table: "M2MConnectivitySettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProfileFingerprint",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderKey",
                table: "M2MConnectivitySettings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SecretBindingFingerprint",
                table: "M2MConnectivitySettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SecretBindingRevision",
                table: "M2MConnectivitySettings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IntegrationProviderSettingsDuplicateArchives",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProviderTable = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OriginalId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ProtectedClientSecret = table.Column<string>(type: "text", nullable: true),
                    ProtectedDeviceToken = table.Column<string>(type: "text", nullable: true),
                    ProfileFingerprint = table.Column<string>(type: "text", nullable: true),
                    SecretBindingFingerprint = table.Column<string>(type: "text", nullable: true),
                    SecretBindingRevision = table.Column<int>(type: "integer", nullable: true),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationProviderSettingsDuplicateArchives", x => x.Id);
                });

            migrationBuilder.Sql("UPDATE \"M2MConnectivitySettings\" SET \"ProviderKey\" = 'Orchestrator' WHERE \"ProviderKey\" = '';");
            migrationBuilder.Sql("UPDATE \"NetclawConnectivitySettings\" SET \"ProviderKey\" = 'Netclaw' WHERE \"ProviderKey\" = '';");
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "ProviderKey" ORDER BY "Revision" DESC, "Id") AS rn
                    FROM "M2MConnectivitySettings"
                )
                INSERT INTO "IntegrationProviderSettingsDuplicateArchives"
                    ("ProviderTable", "OriginalId", "ProviderKey", "Revision", "ProtectedClientSecret", "ProfileFingerprint", "SecretBindingFingerprint", "SecretBindingRevision", "ArchivedAtUtc")
                SELECT 'M2MConnectivitySettings', s."Id", s."ProviderKey", s."Revision", s."ProtectedClientSecret", s."ProfileFingerprint", s."SecretBindingFingerprint", s."SecretBindingRevision", CURRENT_TIMESTAMP
                FROM "M2MConnectivitySettings" s
                INNER JOIN ranked r ON r."Id" = s."Id"
                WHERE r.rn > 1;
                DELETE FROM "M2MConnectivitySettings"
                WHERE "Id" IN (
                    SELECT "Id" FROM (
                        SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "ProviderKey" ORDER BY "Revision" DESC, "Id") AS rn
                        FROM "M2MConnectivitySettings"
                    ) ranked WHERE rn > 1
                );
                """);
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "ProviderKey" ORDER BY "Revision" DESC, "Id") AS rn
                    FROM "NetclawConnectivitySettings"
                )
                INSERT INTO "IntegrationProviderSettingsDuplicateArchives"
                    ("ProviderTable", "OriginalId", "ProviderKey", "Revision", "ProtectedDeviceToken", "ProfileFingerprint", "SecretBindingFingerprint", "SecretBindingRevision", "ArchivedAtUtc")
                SELECT 'NetclawConnectivitySettings', s."Id", s."ProviderKey", s."Revision", s."ProtectedDeviceToken", s."ProfileFingerprint", s."SecretBindingFingerprint", s."SecretBindingRevision", CURRENT_TIMESTAMP
                FROM "NetclawConnectivitySettings" s
                INNER JOIN ranked r ON r."Id" = s."Id"
                WHERE r.rn > 1;
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
