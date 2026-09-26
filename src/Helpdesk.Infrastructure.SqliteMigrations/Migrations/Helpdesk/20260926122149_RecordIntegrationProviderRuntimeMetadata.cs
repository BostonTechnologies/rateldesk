using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class RecordIntegrationProviderRuntimeMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAppliedAtUtc",
                table: "NetclawConnectivitySettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAppliedAtUtc",
                table: "M2MConnectivitySettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAppliedAtUtc",
                table: "NetclawConnectivitySettings");

            migrationBuilder.DropColumn(
                name: "LastAppliedAtUtc",
                table: "M2MConnectivitySettings");
        }
    }
}
