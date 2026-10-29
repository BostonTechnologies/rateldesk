# Roles and tenant-scoped access

RatelDesk resolves access on the API from an application principal, its durable
role assignments, and the target organization. A role or organization claim
sent by a browser is not trusted as an authorization decision.

## Built-in roles

| Role | Scope | Access |
| --- | --- | --- |
| Instance Administrator | Instance | Full instance administration, including roles, tenant administration, and local accounts. It remains compatible with the `HelpdeskAdmin` role. |
| Tenant Administrator | Each assigned tenant | Tenant user, role-assignment, settings, customer/contact, and SLA-management permissions for that tenant. It has no database, identity-provider, or instance-administrator access. |
| Technician | Each assigned tenant | Incident, request and change read/write; change approval; self-service. Deletion is a separate permission. |
| Incident / Request / Change Reader | Each assigned tenant | Read all records of the selected module in that tenant, including staff conversation history; no mutation. |
| Incident / Request / Change Writer | Each assigned tenant | Read, create and update that module in the tenant. Deletion and change approval remain separate. |
| Request Executor | Each assigned tenant | Read requests and execute orchestration actions; Request Writer does not imply execution. |
| Change Approver | Each assigned tenant | Read changes and approve a submitted change; cannot otherwise edit or implement it. |
| Self-service User | Own resources in each assigned tenant | The self-service, incident-user, and request-user permission bundle. |

Built-in roles are protected. Instance administrators can create tenant-owned
custom roles in **Administration → Roles & Permissions**. A custom role can
only contain the published assignable permissions, and write, delete and approval permissions must
also include the corresponding tenant-wide `.Read` permission. `.User` remains
limited to the linked contact’s own records; it is not a tenant-wide reader. A tenant administrator
can use **Tenant roles** to create, edit, or delete custom roles owned by an
assigned tenant only. Its delegation ceiling is deliberately smaller: self
service and the incident, request, and change read/write/delete/approval permissions. It
cannot delegate tenant-management, data-management, or instance permissions.

## Tenant membership delegation

The tenant Team workflow is available at **Administration → Accounts & Orgs →
Team** (`/admin/users`) when the API resolves at least one tenant with
`Tenant.Users.Manage` or `Tenant.Roles.Assign`. It lists only members of the
selected authorized tenant. Role editing requires `Tenant.Roles.Assign`; local
invitations require both `Tenant.Users.Manage` and `Tenant.Roles.Assign` for
the same tenant. Compact member rows show only delegable assignments, and the
reusable editor keeps an independent draft until Save. The invitation
response contains a one-time activation token; the administrator must share it
through an approved secure channel.

This workflow deliberately cannot alter an existing global account, reset
credentials, enable or disable accounts, grant instance access, expose
non-delegable role assignments, or replace memberships in another tenant. A
new invitation always creates a non-administrator local account with only the
self-service assignment in the selected tenant. A pre-existing tenant-administrator assignment is preserved when delegated
access changes. Operational Technician grants can be assigned or removed
inside the selected tenant.
Instance administrators use the full Team administration flow for all broader
account actions.

The API remains authoritative for the page and all direct requests:

- `GET /api/v1/tenant-admin/organizations?permission=…` accepts only supported
  tenant-management permissions and returns only enabled tenants for that
  exact permission (or all enabled tenants to an instance administrator).
- Tenant member and membership routes require the same tenant-scoped
  permission and reject users outside that tenant and instance administrators.
- The membership route accepts operational built-ins and custom tenant roles only
  when their owner matches the selected tenant and their permissions remain
  within the delegation ceiling; it preserves every non-delegable assignment.

Membership changes increment the target local account's authorization revision
and security stamp. Its next API request is rejected until it signs in again.
The Web application validates a local browser session against that API decision
on its next HTTP request and removes a rejected cookie, so revoked tenant
access does not persist in an existing API or Web session.
Invitation and membership-change audit records contain the actor, target, and
tenant-scoped action, but never the activation token or a credential.

Custom-role creation, updates, and deletion are also recorded with the acting
principal, role target, tenant scope, and the permission set (including the
before/after set for an update). These records contain authorization metadata
only; they never include credentials, activation tokens, or session material.

## External identities

OIDC identities and local accounts use the same resolved role and scope model.
External identity links are based on verified provider identity evidence; email
is profile data and is not an authorization-link key. The Web client may use
the effective-access projection to decide which navigation to display, but the
API re-resolves access for every protected operation. On every authenticated
OIDC Web request, the Web cookie is refreshed from that API projection: a
scoped-role grant or revocation takes effect for both API operations and Web
navigation on the next request. Local-account membership changes instead
invalidate the account's authorization revision and security stamp, causing the
next API request to reject the old local session.

## Owner administration through HTTP MCP

An instance administrator can explicitly authorize an account-bound HTTP MCP
agent to administer the instance on their behalf. In **Integration → API & MCP
credentials**, create an **HTTP MCP gateway** connection, choose **Owner
administration** under **Server access**, and approve the instance-wide
consent on **Review & create**. **Tenant access** sets the default context and
**Policies** shows the proposed policy bounds before that final approval. The
browser pairing consent page offers the same owner-administration choice to a
current instance administrator. Ordinary clients start with selected permissions
in one organization; upgrading the application does not upgrade their grants.

The owner grant includes ticket, incident, request, change, self-service,
automation, system-connection, settings, and account administration through the
same API policies used by a human instance administrator. It applies across all
organizations, including organizations created later. The chosen organization is
the default context. If that organization becomes disabled or is removed, status
returns a null default instead of silently selecting another one. Global
administration remains available to repair or create organizations, including
when no organization is currently enabled. Operations that require a target
organization still require its explicit identifier.

Both Local and linked OIDC application accounts can authorize this access.
Authentik is not required. Approval and every subsequent HTTP-MCP exchange/API
execution check that the persisted account remains enabled and is currently an
instance administrator. Removing that authority, disabling the account, revoking
or deleting the credential, or reaching its expiry invalidates access on the
next request, including execution tokens already issued by the gateway.

| Credential kind | Effective access | Who can issue it |
| --- | --- | --- |
| Ordinary API or MCP | Selected current permissions in one enabled organization | An enabled account with those permissions |
| Owner administration | Current instance administrator authority across organizations | An enabled persisted instance administrator with explicit owner consent |
| Temporary setup verification | Authenticated status only for its reviewed organization; five minutes | An account authorized for that organization |

The browser's HTTP MCP creation workflow also reviews an operation base policy:
one organization scope for an ordinary credential, or **Server / instance** for
explicit owner administration. **All access** means the operations supported by
the selected grant and current owner authority. A policy never creates a role
assignment, adds an organization to an ordinary credential, or replaces the
owner-administration consent.

Nested operation-deny exceptions narrow the parent's scope. Deny takes
precedence, and parent expiry or retirement prevents child admission. Retired
policies retain their audit history; deleting a policy does not restore
unconstrained access. Existing flat organization/permission ceilings preserve
their original semantics. See [policy setup and
management](../mcp-http/setup.md#owner-permissions-and-mcp-policies) for the
operator workflow and the distinction between grants and policy admission.

The public create/pairing contract uses `purpose: "mcp"`,
`grantKind: "owner-administration"`, `permissions: ["HelpdeskAdmin"]`, and
`ownerAdministrationConsent: true`. The API stores the distinct `mcp-owner`
purpose in its credential and pairing records. Existing owner-administration
credentials retain this purpose; the setup and policy upgrade does not
retroactively convert ordinary grants. The public delivery identifies
`grantKind: "owner-administration"`; the gateway execution receipt also includes
`isHelpdeskAdmin: true` and the current enabled organization identifiers.

`mcp-owner` credentials are bound to the exact HTTPS `/mcp` resource. They are
accepted only at the gateway's API exchange, which issues a short-lived,
API-only execution token. Presenting the opaque credential at a business API,
presenting an API-purpose credential to the exchange, or using another resource
does not confer access. Setup verification cannot be upgraded to owner
administration. A new owner credential requires its own explicit selection and
consent; replacement and revocation follow the existing credential lifecycle.
The confirmed `helpdesk_auth/revoke_self` operation revokes only the presenting
API or MCP credential. Its next authenticated request is rejected.

Creating, approving and issuing further integration credentials remains an
interactive account consent operation. An owner agent can administer ordinary
business connections using its persisted human owner attribution, but this does
not turn the agent into an interactive login or allow it to mint further
delegation credentials through the browser consent endpoints.

Confirmed local-account creation and activation-token generation may deliver a
newly generated activation token to an authorized owner agent, matching the human
administrator workflow. These responses use `Cache-Control: no-store` and
`sensitiveFields: ["activationToken"]`. The token is operational secret delivery,
not a diagnostic or read of an existing password. Default object string output,
previews, audit summaries and public campaign evidence exclude its value. The
agent's approved private secret handoff should carry it to the intended account
activation workflow.

## Permission and upgrade behavior

Application-managed accounts have explicit assignments. Removing the last
assignment removes all application grants; a historical `User.Role`, linked
contact, or previously issued role claim never recreates them. The effective
permission projection preserves `(permission, organization)` pairs, including
when a user is a writer in one tenant and a self-service user in another.

For rc.3 OIDC compatibility, explicit upstream `*.Manager` permissions and the
configured Technical group retain their existing capabilities, including deletion.
They are resolved only for the verified linked organization and its configured
support organizations. New application-managed Technician roles use the narrower
read/write/approval bundle. A legacy external link receives its original
self-service assignment once when provisioning links its application user;
subsequent logins do not recreate removed assignments. Provider grants remain
an independent source and must be removed at the provider to revoke them.

A signed change-approval email link delegates a decision to its particular
recipient and change. `Change.Approve` separately permits the authenticated
tenant-wide approval action; neither a reader nor a writer role implies it.

## Tenant display settings

`Tenant.Settings.Manage` enables **Tenant settings** for the assigned
organization. It can change the organization name and contact information.
The endpoint deliberately exposes only these fields; identity-provider,
external orchestration, database and support-routing configuration remain
instance administration. Updates are recorded with actor and organization.

Request-task approvals use the existing signed, designated-recipient capability.
A Request Writer or Request Executor cannot approve on behalf of that recipient.

## Additional tenant administration capabilities

`Tenant.Customers.Manage` is reserved for tenant-scoped customer/contact
workflows and `Tenant.Sla.Manage` is reserved for tenant SLA configuration,
calendars, policies, and reporting subscriptions. They are built into Tenant
Administrator and reconciled by the protected-role seeder on startup and
role-definition reads, so existing Tenant Administrator assignments gain the
capability without manual database edits or reassignment. They are intentionally
absent from the tenant-administrator delegation ceiling: a tenant administrator
can exercise these capabilities but cannot manufacture them in a custom role.

Incident and request bulk state/assignment changes require Write in every target
organization. Every target and assignee is checked before any record changes;
a batch containing a missing or unauthorized ticket changes nothing. The same
endpoints support inline assignment of a single request.

