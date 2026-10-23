using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.SqliteMigrations.Migrations.Helpdesk
{
    /// <inheritdoc />
    public partial class PersistTimelineWorklogEffort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Hours",
                table: "TicketTimelineEvents",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkLogId",
                table: "TicketTimelineEvents",
                type: "TEXT",
                nullable: true);

            // Legacy captured publications can authoritatively supply effort for
            // the exact event, but cannot supply a missing canonical worklog ID.
            // Invalid JSON is replaced before any extraction; duplicate keys and
            // conflicting snapshots remain unavailable rather than being guessed.
            migrationBuilder.Sql("""
                WITH raw AS MATERIALIZED (
                    SELECT CASE WHEN json_valid(effect."Payload") THEN effect."Payload" ELSE '{}' END AS payload,
                        receipt."TicketId" AS receipt_ticket, receipt."OrganizationId" AS receipt_organization
                    FROM "MailboxOutboxEffect" effect
                    JOIN "InboundMessageReceipt" receipt ON receipt."Id" = effect."ReceiptId" AND receipt."Outcome" = 1
                    WHERE effect."Kind" = 1 AND effect."ReceiptId" IS NOT NULL
                        AND effect."EffectKey" GLOB '[0-9][0-9][0-9][0-9]:Timeline'
                        AND substr(effect."EffectKey", 1, 4) BETWEEN '0000' AND '0255'
                ), captured AS MATERIALIZED (
                    SELECT json_extract(payload, '$.Id') AS event_id,
                        json_extract(payload, '$.TicketId') AS ticket_id,
                        json_extract(payload, '$.EventType') AS event_type,
                        CASE WHEN json_type(payload, '$.Hours') IN ('integer', 'real')
                            AND json_extract(payload, '$.Hours') BETWEEN -1.7976931348623157e308 AND 1.7976931348623157e308
                            AND NOT EXISTS (SELECT 1 FROM json_each(payload) GROUP BY lower(key) HAVING count(*) > 1)
                            THEN json_extract(payload, '$.Hours') ELSE NULL END AS hours,
                        receipt_ticket, receipt_organization
                    FROM raw
                    WHERE json_type(payload) = 'object'
                        AND json_type(payload, '$.Id') = 'text'
                        AND json_type(payload, '$.TicketId') = 'text'
                        AND json_type(payload, '$.EventType') = 'integer'
                        AND json_extract(payload, '$.EventType') IN (0, 7)
                        AND NOT EXISTS (SELECT 1 FROM json_each(payload)
                            WHERE lower(key) IN ('id', 'ticketid', 'eventtype') GROUP BY lower(key) HAVING count(*) > 1)
                ), unambiguous AS (
                    SELECT target."Id", min(captured.hours) AS hours
                    FROM "TicketTimelineEvents" target
                    JOIN "Tickets" ticket ON ticket."Id" = target."TicketId"
                    JOIN captured ON lower(captured.event_id) = lower(CAST(target."Id" AS TEXT))
                        AND captured.ticket_id = target."TicketId"
                        AND captured.event_type = target."EventType"
                        AND captured.receipt_ticket = target."TicketId"
                        AND captured.receipt_organization = ticket."OrganizationId"
                    WHERE target."Hours" IS NULL AND target."EventType" IN (0, 7)
                    GROUP BY target."Id"
                    HAVING count(captured.hours) = count(*) AND min(captured.hours) = max(captured.hours)
                )
                UPDATE "TicketTimelineEvents"
                SET "Hours" = (SELECT hours FROM unambiguous WHERE unambiguous."Id" = "TicketTimelineEvents"."Id")
                WHERE "Hours" IS NULL AND "Id" IN (SELECT "Id" FROM unambiguous);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Hours",
                table: "TicketTimelineEvents");

            migrationBuilder.DropColumn(
                name: "WorkLogId",
                table: "TicketTimelineEvents");
        }
    }
}
