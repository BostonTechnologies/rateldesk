# Incident-only service-link profile

Capability: `bostec.service-link.incident-only.v1` over the unchanged `bostec.service-link.v1` wire contract.

Both endpoint snapshots must advertise this profile before a new incident-only attempt is provisioned. A peer lacking it yields `upgrade-required`. Its absence never changes historical grants, schemas, canonical hash projections or the frozen v1 conformance vectors.

The NetRatel-targeted direction grants exactly `bostec.service-link.control` and `bostec.service-link.verify`, with exactly this capability, the approved reciprocal tenant pair, and zero agents/request definitions. It authorizes only the existing link lifecycle and verification endpoints. Empty resources still confer no business authority. Orchestration read/invoke and catalog/task access remain denied.

The RatelDesk-targeted direction independently grants `rateldesk.incidents.create`, `rateldesk.incident-receipts.read` and `rateldesk.incident-targets.read`, one approved organization/customer, and the exact persisted NetRatel producer/source namespace. Human consent, S256 proof, attempt-bound expiring pairing code, immutable grant hash, credential exchange, two authenticated verification receipts, shared durable commit, replay recovery, rotation and revocation retain their v1 meanings.

Optional approved task execution uses the existing independently scoped orchestration grant and callback capability; it does not inherit business access from this profile. Existing v1 business grants keep working during either product's upgrade. Upgrade RatelDesk then NetRatel before creating incident-only links; an existing link needs explicit new consent to change its permissions.

RatelDesk retains the reverse control credential in its existing protected attempt and does not create or enable an orchestration provider. Its business-sender status truthfully remains false; both peers prove active inbound authority and committed verification instead. NetRatel activates a tenant-owned managed Flow connector with the approved receiver mapping and persisted human owner's current authority. Existing receiver readiness probes verify authenticated capabilities and targets and do not create incidents. Temporary outage reports readiness unavailable without deleting the approved connection.

Authenticated NetRatel status for this negotiated profile adds `incident_delivery_ready`, an inert observation of the current authorized managed Flow reference and existing receiver readiness. RatelDesk's administrator Test connection uses the bound status GET and local receiver authority; it does not issue lifecycle writes, tasks, or incidents. Missing or false readiness remains a repairable connection state. The extension is outside v1 approval hashes and is not present on historical grants.
