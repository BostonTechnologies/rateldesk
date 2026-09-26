using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class PersistIntegrationProviderSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CatalogPath",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HealthPath",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IngestPath",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "LastTestSucceeded",
                table: "M2MConnectivitySettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastTestedAtUtc",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedClientSecret",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteScope",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "M2MConnectivitySettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "NetclawConnectivitySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Instance = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ProtectedDeviceToken = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    AllowPrivateHttp = table.Column<bool>(type: "INTEGER", nullable: false),
                    IdleMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectionCapacity = table.Column<int>(type: "INTEGER", nullable: false),
                    TurnInactivityTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ActivityHeartbeatIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    LastTestedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastTestSucceeded = table.Column<bool>(type: "INTEGER", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetclawConnectivitySettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetclawConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "CatalogPath",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "HealthPath",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "IngestPath",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "LastTestSucceeded",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "LastTestedAtUtc",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "ProtectedClientSecret",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "RemoteScope",
                table: "M2MConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "M2MConnectivitySettings");
        }
    }
}
