using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
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
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HealthPath",
                table: "M2MConnectivitySettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "IngestPath",
                table: "M2MConnectivitySettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "LastTestSucceeded",
                table: "M2MConnectivitySettings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastTestedAtUtc",
                table: "M2MConnectivitySettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedClientSecret",
                table: "M2MConnectivitySettings",
                type: "character varying(8192)",
                maxLength: 8192,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteScope",
                table: "M2MConnectivitySettings",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "M2MConnectivitySettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "NetclawConnectivitySettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Instance = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ProtectedDeviceToken = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    AllowPrivateHttp = table.Column<bool>(type: "boolean", nullable: false),
                    IdleMinutes = table.Column<int>(type: "integer", nullable: false),
                    ConnectionCapacity = table.Column<int>(type: "integer", nullable: false),
                    TurnInactivityTimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    ActivityHeartbeatIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    LastTestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastTestSucceeded = table.Column<bool>(type: "boolean", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
