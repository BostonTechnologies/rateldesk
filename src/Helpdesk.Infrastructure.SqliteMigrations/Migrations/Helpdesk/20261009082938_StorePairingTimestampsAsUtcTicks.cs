using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class StorePairingTimestampsAsUtcTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Published upgrades create these pairing tables immediately before this migration.
            // Convert any existing valid ISO timestamps explicitly, preserving 100 ns fractions and
            // offsets; malformed authority bounds fail the migration instead of becoming zero.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE "__PairingUtcTicksValidation"
                    ("Ticks" INTEGER NOT NULL CHECK(typeof("Ticks") = 'integer'
                     AND "Ticks" BETWEEN 0 AND 3155378975999999999));
                """);
            foreach (var (table, column) in new[]
            {
                ("SystemPairs", "UpdatedAtUtc"),
                ("SystemConnections", "UpdatedAtUtc"),
                ("SystemConnections", "LastTestedAtUtc"),
                ("PairingRedemptions", "RetryUntilUtc"),
                ("PairingCleanups", "ExpiresAtUtc"),
                ("InstallationPairingCodes", "ExpiresAtUtc")
            })
            {
                // Identifiers come only from the fixed list above. Removing the fraction before
                // strftime avoids SQLite rounding .9999999 into the next second.
                var value = $"\"{column}\"";
                var zone = $"CASE WHEN substr({value}, -6, 1) IN ('+', '-') THEN substr({value}, -6) WHEN substr({value}, -1) = 'Z' THEN 'Z' ELSE NULL END";
                var zoneLength = $"CASE WHEN substr({value}, -6, 1) IN ('+', '-') THEN 6 WHEN substr({value}, -1) = 'Z' THEN 1 ELSE 0 END";
                var fractionText = $"substr({value}, 21, length({value}) - 20 - ({zoneLength}))";
                var fraction = $"CASE WHEN substr({value}, 20, 1) = '.' THEN CAST(substr(({fractionText}) || '0000000', 1, 7) AS INTEGER) ELSE 0 END";
                var validShape = $"substr({value}, 1, 19) GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9][ T][0-9][0-9]:[0-9][0-9]:[0-9][0-9]'";
                var validCalendar = $"strftime('%Y-%m-%d', substr({value}, 1, 10), '+0 days') = substr({value}, 1, 10) AND CAST(substr({value}, 12, 2) AS INTEGER) BETWEEN 0 AND 23 AND CAST(substr({value}, 15, 2) AS INTEGER) BETWEEN 0 AND 59 AND CAST(substr({value}, 18, 2) AS INTEGER) BETWEEN 0 AND 59";
                var validZone = $"(({zoneLength}) = 1 OR (({zoneLength}) = 6 AND substr({value}, -6) GLOB '[+-][0-9][0-9]:[0-9][0-9]' AND CAST(substr({value}, -5, 2) AS INTEGER) BETWEEN 0 AND 14 AND CAST(substr({value}, -2) AS INTEGER) BETWEEN 0 AND 59 AND (CAST(substr({value}, -5, 2) AS INTEGER) < 14 OR substr({value}, -2) = '00')))";
                var validFraction = $"(length({value}) = 19 + ({zoneLength}) OR (substr({value}, 20, 1) = '.' AND length({fractionText}) BETWEEN 1 AND 7 AND ({fractionText}) NOT GLOB '*[^0-9]*'))";
                var ticks = $"CASE WHEN {validShape} AND {validCalendar} AND {validZone} AND {validFraction} THEN CAST(strftime('%s', substr({value}, 1, 19) || ({zone})) AS INTEGER) * 10000000 + 621355968000000000 + ({fraction}) END";
                migrationBuilder.Sql($"INSERT INTO \"__PairingUtcTicksValidation\" (\"Ticks\") SELECT {ticks} FROM \"{table}\" WHERE {value} IS NOT NULL;");
                migrationBuilder.Sql($"UPDATE \"{table}\" SET {value} = {ticks} WHERE {value} IS NOT NULL;");
            }
            migrationBuilder.Sql("DROP TABLE \"__PairingUtcTicksValidation\";");

            migrationBuilder.AlterColumn<long>(
                name: "UpdatedAtUtc",
                table: "SystemPairs",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<long>(
                name: "UpdatedAtUtc",
                table: "SystemConnections",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<long>(
                name: "LastTestedAtUtc",
                table: "SystemConnections",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "RetryUntilUtc",
                table: "PairingRedemptions",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<long>(
                name: "ExpiresAtUtc",
                table: "PairingCleanups",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<long>(
                name: "ExpiresAtUtc",
                table: "InstallationPairingCodes",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Pairing authority migrations are forward-only. Restore the pre-upgrade database and protected-key backup to roll back.");
        }
    }
}
