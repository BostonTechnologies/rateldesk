using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class RecordGraphDatasetImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GraphDatasetImportRuns",
                columns: table => new
                {
                    OrganizationId = table.Column<string>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActiveOrganizationId = table.Column<string>(type: "TEXT", nullable: true),
                    ConnectionFingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigurationSaved = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SourceResultsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GraphDatasetImportRuns", x => new { x.OrganizationId, x.RequestId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_GraphDatasetImportRuns_ActiveOrganizationId",
                table: "GraphDatasetImportRuns",
                column: "ActiveOrganizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GraphDatasetImportRuns_OrganizationId_StartedAtUtcTicks",
                table: "GraphDatasetImportRuns",
                columns: new[] { "OrganizationId", "StartedAtUtcTicks" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GraphDatasetImportRuns");
        }
    }
}
