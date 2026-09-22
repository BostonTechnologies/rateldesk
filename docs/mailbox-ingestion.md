# Inbound mailbox administration

An instance administrator configures ingress at `/admin/email-settings`. Provider selection is independent for every connection. The application organization is separate from the Microsoft directory ID (`tenantId` in the compatibility request contract).

| Provider | Authentication | Source identity | Disposition |
| --- | --- | --- | --- |
| Microsoft Graph | Microsoft application directory/client ID and secret | Immutable Graph message ID, folder delta checkpoint | Mark read; optionally move to a configured folder ID |
| IMAP | Password/app password or Microsoft application OAuth | Configured folder, UIDVALIDITY and UID | Mark seen; optional MOVE when the server supports it |
| POP3 | Password/app password or Microsoft application OAuth | Stable UIDL | Leave messages on server |

Password authentication only works where the server permits it. Microsoft protocol OAuth requires Exchange protocol application permissions and mailbox authorization; Graph consent does not grant IMAP/POP access. Graph reads MIME directly and does not connect to IMAP. See [Microsoft protocol OAuth](https://learn.microsoft.com/en-us/exchange/client-developer/legacy-protocols/how-to-authenticate-an-imap-pop-smtp-application-by-using-oauth), [Graph delta synchronization](https://learn.microsoft.com/en-us/graph/delta-query-messages), and [immutable Graph IDs](https://learn.microsoft.com/en-us/graph/outlook-immutable-id).

Graph read/move processing requires application `Mail.ReadWrite` access restricted to the intended mailboxes by the Microsoft administrator. Inbound testing does not need `Mail.Send`. A read-only connection test proves authentication and folder access, not permission to move mail. IMAP and POP require verified TLS on connect or mandatory STARTTLS; opportunistic TLS and certificate bypasses are not supported.

## Global inheritance and dedicated overrides

There is one global assignment and at most one dedicated assignment per organization, including disabled assignments. Ten organizations inheriting the global mailbox and three using dedicated Graph/IMAP/POP3 mailboxes use four connections. Each dedicated connection operates independently when global ingress is absent, paused or unavailable.

Pausing a dedicated connection keeps its assignment. Failure never switches it to global automatically. **Revert to global mailbox** explicitly archives the dedicated assignment; receipts and rule history remain. Global mail for an organization with a dedicated assignment is held as `TenantUsesDedicatedMailbox`, without provisioning a customer or creating a ticket. Correct the routing/assignment, then retry through inbound diagnostics.

Configure external delivery and announce dedicated addresses before cutover. This application does not change MX records or forward messages between mail providers. Replies to older tickets may still arrive at the old global address and be held. Review existing organization branding Reply-To before switching ingress; conflicting Reply-To must be reconciled. Outbound From and Microsoft sending credentials remain separately configured through `ExchangeEmail`. Generic protocol ingestion does not configure an SMTP sender.

## Import, retention and source changes

New connections default to new messages only. Importing historical messages requires explicit confirmation. Existing unread import is available for Graph and IMAP; POP has no unread state. Migrated hybrid settings retain unread-backlog behavior and their original GUID and enabled/background flags. Additional historic settings are archived and cannot poll.

Changing provider, server, account, folder or organization requires archiving and creating a new source. Credential rotation on an unchanged source preserves progress. IMAP UIDVALIDITY changes create a new epoch: messages already present are captured for review without business processing, while later arrivals continue normally. Review historical tickets before explicitly retrying epoch-reset messages. POP requires UIDL, retains all messages and provides no folder/read-state/delete controls. Operators must manage server retention. The protocol adapter limits POP enumeration to 10,000 entries; mailbox receipt storage is capped at 100,000 deliveries pending operator retention maintenance.

## Credentials and network policy

Responses expose credential-present flags, never stored credentials. Empty credentials retain the saved value on an unchanged source. A changed draft destination cannot borrow a saved secret. Credential clear requests are explicit and cannot leave an enabled connection without credentials.

Mailbox credentials and captured normalized message envelopes use the existing Data Protection key ring, with a separate mailbox-specific versioned purpose. Preserve `DataProtection:ApplicationName` and persist/share `DataProtection:KeyRingPath` across replicas. Back up the key ring with the database. Missing keys fail closed and require recovery or re-entry; ciphertext is never used as plaintext.

Both draft tests and workers apply `EmailIngestion:AllowedPrivateHosts`, an operator-controlled array of exact private mail-server hostnames. Public server addresses are permitted; unapproved private/loopback addresses and metadata/link-local destinations are denied. DNS addresses are validated and the connection is pinned to the validated address. Use network egress controls as an additional deployment boundary. Only instance administrators may configure connections, tests, bindings and retries.

## Processing and diagnostics

Both mailbox switches must be enabled, and the deployment-wide `EmailIngestion:Enabled` switch must permit processing. Healthy reconciliation runs every five seconds. Up to four independent mailbox operations run concurrently. Database leases, fencing and source-version checks prevent obsolete workers from committing after ownership changes. Replicas need synchronized UTC clocks.

Source capture and checkpoint advancement share a database transaction. Ticket/rule changes and durable notification work share a separate fenced transaction. Provider acknowledgment happens after business commit and can be retried independently. A receipt distinguishes pending, succeeded, ignored, held and failed outcomes. Inbound diagnostics show state and safe retries without exposing message bodies. `/admin/pending-emails` remains the existing outbound delivery view.

Notification dispatch uses a bounded durable outbox. External mail is at-least-once when the provider accepts a send but its response is lost; local ticket changes are not rerun for that uncertainty. Persisted notifications are shared, while the existing live notification buses remain per replica.

## Rule scope and source binding

Tenant rules run before global fallback rules, with priority and stable tie-breaking within each tier. **All eligible mailboxes** preserves reusable rules. A source binding restricts eligibility; it does not change priority or grant tenant authority. Global rules may target a dedicated source; a tenant cannot target another tenant's dedicated source. Reordering operates within one scope/organization/source bucket. Explicit priorities determine how different buckets interleave.

The forwarded-support template remains disabled until explicitly enabled. Authorized forwarded incidents preserve their original requester and New/Unassigned behavior. Ownership conflicts, ambiguous routing and unconfident requester parsing require review. Existing customers are never moved between organizations by ingress.

## Upgrade and recovery

Back up both databases, attachment storage and the shared Data Protection key ring before upgrade. Stop old application replicas before migration so the retired hybrid worker cannot consume alongside the coordinator. Both PostgreSQL and SQLite have additive mailbox/receipt/lease/outbox schema migrations. Application-side credential protection runs before workers and has a durable completion marker; restarts do not recreate removed assignments.

The upgrade preserves historical data for previously retired application features; mailbox migration does not delete those unrelated tables. Review archived mailbox rows and explicit outbound identity, test the saved global connection, then enable any new sources deliberately. The schema down migration is intentionally rejected: restoring the pre-upgrade database and key backup is a recovery operation, not a lossless rollback after processing new mail.
