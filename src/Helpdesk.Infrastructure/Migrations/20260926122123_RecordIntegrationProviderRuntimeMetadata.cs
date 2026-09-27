using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
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
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAppliedAtUtc",
                table: "M2MConnectivitySettings",
                type: "timestamp with time zone",
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
