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
