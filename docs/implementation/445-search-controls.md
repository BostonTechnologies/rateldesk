# Organization and contact lookup inventory

This register tracks [issue #445](https://github.com/BostonTechnologies/rateldesk/issues/445). The common control is [CreationSearchPicker](../../src/HelpDesk.NewWeb/Components/Shared/CreationSearchPicker.razor). Its typed wrappers accept the feature's authorized directory and keep stable selected IDs. The ticket creation endpoints continue to distinguish organizations from customer contacts.

| Surface | Editable scope and data source |
| --- | --- |
| Organization Chat | The left workspace header uses the Chat read-grant search. Startup resolves the route ID or requests the first authorized row. Changing organization routes through the existing chat navigation and draft handling. |
| Investigations and Prompts | The header uses the investigation management grants or instance administrator directory respectively. Clearing Investigations includes all accessible organizations. Prompts retains its editor discard guard. |
| Incident, request, change and task lists | The shared lookup uses ticket filter options. Tenant candidates come from authorized tickets. Requesters and assignees use their original dimension, tenant restrictions and selected-ID hydration. |
| Ticket creation and edits | OrganizationSearchPicker and CustomerRequesterPicker use the ticket module and selected organization. TicketCustomerPicker reuses the contact wrapper for unassigned requester edits. ChangeDetail uses the change module. A customer created in a ticket's locked scope shows the organization read-only. |
| People and directory pages | Users, customers, organizations and categories use the directory filter. User create/edit assignments use enabled organizations. Customer create/edit uses the administrative directory. A linked login locks the existing organization. |
| Memberships, roles and tenant settings | The membership chooser uses the union of membership management grants. Tenant settings, delegated credentials and owning-role context each request their own grant. Role ownership is fixed on edit. |
| Organization editor | The manager lookup excludes the edited organization. The external tenant mapping searches the orchestration provider's tenant catalogue. Existing saved names and original linkage timestamps survive a missing provider record or read failure. |
| Support Routing | The selected organization, coverage customer/provider, support-group owner and owner filter use administrative search. Notification subscriptions inherit the current customer organization. The recipient target is an agent or group. |
| Service Levels | Settings, policy and calendar tenant controls use enabled tenants. Fixed tenant dialogs display their parent scope. Reporting looks up staff and enabled contacts in the selected tenant and retains independently selected email targets. The instance SLA dashboard can clear its filter to all tenants. |
| Mailboxes and Pending Mail | A new dedicated mailbox excludes organizations with another active dedicated mailbox, including paused dedicated mailboxes. A saved mailbox displays its immutable owner. Pending Mail's searchable organization filter preserves the applied query and its friendly label. |
| Email Rules and Templates | Rule cloning clears to the global scope. Audit supports authorized names and exact organization IDs. Template and HTML previews clear to system defaults. Internal incident templates use enabled organization search. Layout selection and mailbox binding retain their own identities. |
| Pairing and credentials | Connection choices preserve the authenticated pair and selected parent organization for customer lookup. The external peer supplies its tenant catalogue. Pairing consent keeps the reviewed permission ceiling, clears consent on scope change and posts the chosen ID in the existing antiforgery-protected form. The credential wizard uses admin or delegated settings grants. |
| AI Providers and MCP Policies | Provider editors use the administrative directory and retain their setup locks. Policy scope and owner derive from the chosen credential. The policy editor displays that scope read-only. |
| Automation, catalog and task editors | Rules have no organization field. OrgAccessDialog appends selected organizations to the service audience and preserves the removable chip list. Form primary organization changes through its existing connection reset logic. Approval candidate search covers agents and customer contacts and keeps selected targets across searches. The job library receives definitions already filtered to the exact verified peer tenant ID. |
| Webhooks | The organization picker uses enabled tenant search. The editor locks scope until it is closed. A list response applies only to the scope that issued it. |
| Provider identifiers | Microsoft Entra directory IDs and OpenAI provider organization strings identify accounts in external systems. They remain inputs for the respective provider setup contract. |

The API contract for system pairing and the external orchestration catalogue supplies a full peer catalogue. The Web search endpoints filter these authorized rows before returning a bounded page. The pairing editor resolves only the saved mapping IDs and route hint. New searchable selections are validated in the current pair and the peer rechecks the mapping on save.

## Verification

CreationSearchPickerRenderedTests and OrganizationDirectorySelectorTests exercise stale responses, hydration, failed loads, selected identity and recovery. AiOpsAndPolicyEndpointsTests.Organizations covers permission-scoped identity disclosure and late-page search. OrganizationMappingDialogTests covers old external mappings, renamed tenants and provider failure. AiPromptsRenderedTests exercises dirty-editor guards. PairingConnectionsPageTests exercises selection in the real parent/editor flow.

The PR records executed checks and rendered evidence. [Implementation and verification](https://github.com/BostonTechnologies/rateldesk/pull/461)

Credential setup reads at most two eligible organizations to distinguish zero, one and multiple choices. Its list response includes labels for visible credentials within the caller's management scope. Service audience, role ownership, coverage and email rules resolve labels in batches of up to 25 IDs already referenced on screen. Route-bound settings and memberships resolve the requested ID under the feature's grant. The mailbox overview still loads its effective mailbox configuration rows for display.

The browser runs used synthetic directories with more than 100 authorized organizations. The Chat run typed a late organization, selected it by keyboard, verified the route and checked that no conversation or turn started. The credential wizard selected a late organization and reviewed its exact grants before creation.

[Chat at desktop width](assets/445-search/chat-desktop.png) · [Chat at 390px](assets/445-search/chat-phone.png) · [Credential organization selection](assets/445-search/credential-review.png)

Rendered administration checks selected the 128th synthetic organization on each surface and verified that selecting a filter did not save a configuration. [Support Routing](assets/445-search/lookup-support-routing.png) · [Service Levels](assets/445-search/lookup-tenant-sla-settings.png) · [Automation Webhooks](assets/445-search/lookup-ai-assistant-webhooks.png) · [Pending Mail](assets/445-search/lookup-pending-emails.png)
