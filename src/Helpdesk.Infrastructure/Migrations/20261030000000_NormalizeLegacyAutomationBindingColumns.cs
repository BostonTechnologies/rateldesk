using Helpdesk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations;

[DbContext(typeof(HelpdeskDbContext))]
[Migration("20261030000000_NormalizeLegacyAutomationBindingColumns")]
public sealed class NormalizeLegacyAutomationBindingColumns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Older installations applied the original migration with Sto-prefixed
        // columns. Renaming that migration's source never upgrades those stores.
        migrationBuilder.Sql("""
            DO $compatibility$
            DECLARE
                suffix text;
                legacy_name text;
                current_name text;
            BEGIN
                FOREACH suffix IN ARRAY ARRAY['JobDefinitionId', 'JobDefinitionName', 'RequestDefinitionId', 'RequestDefinitionName']
                LOOP
                    legacy_name := 'Sto' || suffix;
                    current_name := 'Orchestration' || suffix;
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = current_schema() AND table_name = 'AutomationBindings' AND column_name = legacy_name
                    ) THEN
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_schema = current_schema() AND table_name = 'AutomationBindings' AND column_name = current_name
                        ) THEN
                            RAISE EXCEPTION 'AutomationBindings contains both % and %; reconcile the duplicate columns before upgrading.', legacy_name, current_name;
                        END IF;
                        EXECUTE format('ALTER TABLE %I.%I RENAME COLUMN %I TO %I', current_schema(), 'AutomationBindings', legacy_name, current_name);
                    END IF;
                END LOOP;
            END
            $compatibility$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Both current installations and upgraded legacy stores use these names.
        // Rolling this compatibility receipt back must not break their mappings.
    }
}
