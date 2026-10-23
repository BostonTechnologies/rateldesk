using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Helpdesk.Infrastructure.Migrations
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
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkLogId",
                table: "TicketTimelineEvents",
                type: "text",
                nullable: true);

            // A retained ingress publication can preserve the exact event's
            // measured effort even though legacy events lack canonical log IDs.
            // Parse original JSON (not jsonb) so duplicate keys remain visible.
            // The helper is session-local and removed after this one-time scan.
            migrationBuilder.Sql("""
                CREATE FUNCTION pg_temp.rateldesk_legacy_timeline_effort(payload text)
                RETURNS TABLE(event_id uuid, ticket_id text, event_type integer, effort_hours double precision)
                LANGUAGE plpgsql AS $function$
                DECLARE captured json;
                BEGIN
                    captured := payload::json;
                    IF json_typeof(captured) IS DISTINCT FROM 'object'
                        OR json_typeof(captured->'Id') IS DISTINCT FROM 'string'
                        OR json_typeof(captured->'TicketId') IS DISTINCT FROM 'string'
                        OR json_typeof(captured->'EventType') IS DISTINCT FROM 'number'
                        OR EXISTS (SELECT 1 FROM json_each(captured)
                            WHERE lower(key) IN ('id', 'ticketid', 'eventtype') GROUP BY lower(key) HAVING count(*) > 1)
                    THEN RETURN; END IF;
                    event_id := (captured->>'Id')::uuid;
                    ticket_id := captured->>'TicketId';
                    event_type := (captured->>'EventType')::integer;
                    IF event_type NOT IN (0, 7) THEN RETURN; END IF;
                    IF json_typeof(captured->'Hours') = 'number'
                        AND NOT EXISTS (SELECT 1 FROM json_each(captured) GROUP BY lower(key) HAVING count(*) > 1)
                    THEN
                        BEGIN
                            effort_hours := (captured->>'Hours')::double precision;
                            IF effort_hours NOT BETWEEN '-1.7976931348623157e308'::double precision
                                AND '1.7976931348623157e308'::double precision
                            THEN effort_hours := NULL; END IF;
                        EXCEPTION WHEN data_exception THEN effort_hours := NULL;
                        END;
                    END IF;
                    RETURN NEXT;
                EXCEPTION WHEN data_exception THEN
                    RETURN;
                END;
                $function$;

                WITH captured AS MATERIALIZED (
                    SELECT snapshot.*, receipt."TicketId" AS receipt_ticket, receipt."OrganizationId" AS receipt_organization
                    FROM "MailboxOutboxEffect" effect
                    JOIN "InboundMessageReceipt" receipt ON receipt."Id" = effect."ReceiptId" AND receipt."Outcome" = 1
                    CROSS JOIN LATERAL pg_temp.rateldesk_legacy_timeline_effort(effect."Payload") snapshot
                    WHERE effect."Kind" = 1 AND effect."ReceiptId" IS NOT NULL
                        AND effect."EffectKey" ~ '^[0-9]{4}:Timeline$'
                        AND left(effect."EffectKey", 4) BETWEEN '0000' AND '0255'
                ), unambiguous AS (
                    SELECT target."Id", min(captured.effort_hours) AS hours
                    FROM "TicketTimelineEvents" target
                    JOIN "Tickets" ticket ON ticket."Id" = target."TicketId"
                    JOIN captured ON captured.event_id = target."Id"
                        AND captured.ticket_id = target."TicketId"
                        AND captured.event_type = target."EventType"
                        AND captured.receipt_ticket = target."TicketId"
                        AND captured.receipt_organization = ticket."OrganizationId"
                    WHERE target."Hours" IS NULL AND target."EventType" IN (0, 7)
                    GROUP BY target."Id"
                    HAVING count(captured.effort_hours) = count(*)
                        AND min(captured.effort_hours) = max(captured.effort_hours)
                )
                UPDATE "TicketTimelineEvents" target SET "Hours" = unambiguous.hours
                FROM unambiguous WHERE target."Id" = unambiguous."Id" AND target."Hours" IS NULL;

                DROP FUNCTION pg_temp.rateldesk_legacy_timeline_effort(text);
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
