# Automation traceability and internal incident presentation

This additive protocol implements RatelDesk #420 and NetRatel #744. The normative
[schema](rateldesk-incident-create.v2.json), [synthetic fixture](rateldesk-incident-create.v2.fixtures.json)
and checksums are mirrored in both repositories, along with `AutomationTraceContracts.cs`.
The existing [v1 contract](rateldesk-incident-create.v1.md), semantic vectors,
accepted receipts and prepared bytes remain frozen.

## Negotiation and authority

V1 capabilities retain their existing fields. The optional `automationPresentation`
extension advertises `rateldesk.incident-create.v2`, the context schema, immutable
standard presentation, catalogue/preview endpoints and the required narrowly scoped
permission. Consumers must verify this extension through the saved authenticated
connector. Absence means v1 only. Existing published flows stay v1 until an explicit
edit, validation and publication opts the action into standard or custom presentation.

V2 creates use `POST /api/v1/integrations/netratel/incidents` with the existing
`Idempotency-Key` and `X-NetRatel-Source-Instance` headers. Receipt lookup retains the
existing namespace/key route. There is no fallback to ordinary create after a v2
validation, authority or compatibility error. Template catalogue and custom preview are read-only operations, scoped to the approved organization and explicit current template permission through the existing connection approval mechanism. Standard preview uses the existing receipt-read scope.

The authenticated integration service actor, submitted-by person, requested-for
person and configured incident customer are separate identities. Only the saved
mapping determines incident customer/recipients. Origin people do not become
listeners. Display strings and links never grant execution, ticket or output access.

Origin is a top-level negotiated ingress field, outside business input. The sender
snapshots stored instance/organization/request/service/form/task/people and exact
attempt before dispatch. The consumer checks instance, organization, request, task
and correlation against its server-owned admitted binding. Input keys named `user`,
`meta` or `origin` cannot supply this authority. All IDs are JSON strings; request
130, definition 67, parent `639300000000000001`, child runs and flow action IDs are
different entities. The native run correlation and original dispatch correlation
are both retained.

## Immutable presentation and fingerprint

Standard mode pins a versioned built-in presentation. Template mode pins a published
revision, context schema, content digest and frozen expanded layout digest. Neither
mode resolves mutable latest content during retry or replay. Retiring a template
removes it from new selection but keeps existing revisions readable. Revocation
denies new acceptance. A missing, revoked or incompatible revision fails visibly;
there is no implicit template substitution.

V2 fingerprints SHA-256 over the compact UTF-8 System.Text.Json Web serialization
of the typed `AutomationIncidentCreate` in declaration order. All fields, including
origin, exact execution/flow/action, evidence state, presentation and diagnostics,
are semantic. Category IDs normalize to distinct lowercase GUID D strings in
ordinal order; blank assignment normalizes to null. Every other field is preserved.
Malformed shapes, unknown members, duplicate JSON members and invalid bounds are
rejected. No v1 normalization or fingerprint is repurposed for v2.

The receiver renders once inside acceptance and commits incident, customer-safe
summary, internal report HTML/plain text/context, source relation, external resource
references, accepted receipt and durable intended effects in one transaction.
Same-key replay returns the stored result without rendering or creating effects.
Changed context, origin or presentation under that key conflicts. Lost-response
recovery uses the original namespace/key and existing receipt lookup.

Claimed source linkage requires the retained local task/attempt and saved peer.
If delivery precedes the final callback, retained accepted admission evidence must
prove the exact native request/run; otherwise acceptance defers without creating a
partial incident. An unavailable retained origin fails explicitly. Source-less
events return `not-applicable`; they never invent a request relation.

The receipt binds contract, receiver, namespace, producer, key, fingerprint,
organization/customer, incident, flow run/action, presentation and origin relation.
The consumer verifies these before persisting associations. Local index recovery
uses committed receipt evidence and never creates a new incident key. Original
execution outcome and incident action outcome remain independent.

## Visibility, links and budgets

Ordinary subject/summary use only the restricted safe projection: source kind,
request reference/title, service, definition name, source outcome and target totals.
Internal headings/bodies may use the bounded authorized diagnostic projection.
Stderr, private input and internal-note tokens are unavailable to subject templates,
notifications, portal/public serializers and search snippets.

RatelDesk renders and sanitizes internal HTML, encodes scalar values once, forbids
raw diagnostic tokens and produces coherent plain text. Preview and acceptance
use the same renderer. Rendering does not create incidents, executions or email.
Only HTTPS links from configured public origins and implemented routes are emitted;
userinfo, tokens, arbitrary destinations and guessed routes are rejected.

Context is at most 65,536 UTF-8 bytes, 25 targets, 8 failed steps per target, 512
characters per error excerpt, 3 execution links and 2 source links. Selection
prioritizes failed activities before limits. Omitted counts and available,
truncated, redacted or unavailable evidence are explicit. Repeated errors group
for display without losing affected target count. A child exit code does not become
a native aggregate exit code. Optional `supplement` fields retain the bounded source presentation (including monitoring units), separately labelled AI conclusions and recorded recovery disposition. `jobExecution` distinguishes a containing native job definition/run from a script definition and script execution; absent historical values are not inferred. Both supplements are semantic and frozen. Retained output is read through existing authorization
and redaction; template selection is never an output permission.

## Upgrade and activation

Deploy compatible readers/providers before enabling writers. Retain pairings,
protected keys, prepared deliveries and receipts during additive migrations.
Approve the new bounded template permission explicitly where custom presentation
is desired. Edit, validate and publish each flow to opt in; custom selection is off
by default. Backfill only proven retained bindings and verified receipts, in bounded
restartable batches. It does not replay automation or mutate remote tickets.
