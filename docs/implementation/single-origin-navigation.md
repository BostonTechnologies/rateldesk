# Single-origin setup: compact navigation

This records issue [#160](https://github.com/BostonTechnologies/RatelDesk/issues/160), part of [#153](https://github.com/BostonTechnologies/RatelDesk/issues/153). The appearance reference is NetRatel #237, merged through PR #238, at `5718548b33ec27e588f6ecc6503bdff6e128f427`. Its actual navigation component, scoped CSS, layout and rendered active-link tests were reviewed. No owner screenshot file was attached to this task; the pinned source is the available reference.

RatelDesk baseline `a59f2d4c55d0f335fe880c921bde913314a8742d` already has no drawer logo/title block. The application bar, login, browser and tenant branding remain in their existing surfaces. The drawer width changes from 272 to 240 pixels; its rows start at 36 pixels, nested rows at 34 pixels, icons at 20 pixels, and coarse-pointer targets at 44 pixels. Section captions, rounded rows, divider/guide lines, hover, active tint/border/inset stripe and keyboard focus use existing MudBlazor theme variables. Long account names wrap.

## Destination and permission map

The familiar group and leaf labels remain. Work contains Home and the Portal, Workspace, Operations and Resources groups; administration follows; a separated Account area contains the account, Integration credentials, Notifications and Logout links. The complete instance-administrator inventory is 33 leaf anchors, including the new account link. Tenant/global administration are mutually exclusive.

| Group | Leaf destinations | Existing visibility |
| --- | --- | --- |
| Work | `home` | Existing Home link |
| Portal | `self-service`, `self-service/my-requests` | `SelfService.User` or `HelpdeskAdmin` |
| Workspace | `incidents` | Incident User/Read/Write/Manager or `HelpdeskAdmin` |
| Workspace | `requests` | Request User/Read/Write/Execute/Manager or `HelpdeskAdmin` |
| Workspace | `tasks` | Request Read/Write/Execute/Manager or `HelpdeskAdmin` |
| Workspace | `changes` | Change User/Read/Write/Manager or `HelpdeskAdmin` |
| Operations | `/ops/slas` | Incident Write/Manager, Request Write/Manager, Change Write/Manager or `HelpdeskAdmin` |
| Operations | `/ops/workflows` | Request Read/Write/Execute/Manager or `HelpdeskAdmin` |
| Operations | `/ops/hangfire` | `HelpdeskAdmin` |
| Resources | `assets` | `HelpdeskAdmin` |
| Resources | `/admin/resources/data-management` | `DataManagement.Admin` or `HelpdeskAdmin` |
| Global Accounts & Orgs | `/admin/users`, `/admin/customers`, `/admin/organizations`, `/admin/support-routing`, `/admin/tenant-sla-settings` | `HelpdeskAdmin` |
| Global administration | `/admin/roles`, `/admin/categories`, `/admin/branding` | `HelpdeskAdmin` |
| Email Settings | `/admin/email-settings`, `/admin/pending-emails`, `/admin/email-rules`, `/admin/templates`, `/admin/layouts` | `HelpdeskAdmin` |
| Automation | `/admin/automation/integration`, `/admin/automation`, `/admin/requests/services`, `/admin/ai-assistant-webhooks` | `HelpdeskAdmin` |
| Delegated Accounts & Orgs | `/admin/users`, `/admin/organizations` | Existing outer gate `Tenant.Roles.Assign` or `Tenant.Settings.Manage`; Team additionally requires `Tenant.Users.Manage` or `Tenant.Roles.Assign`; excludes `HelpdeskAdmin` |
| Delegated administration | `/admin/roles` | Existing delegated gate plus `Tenant.Roles.Assign`; excludes `HelpdeskAdmin` |
| Account | `auth`, `/account/integration-credentials`, `notifications`, `/logout` | Authenticated |
| Sign-in | `/login` | Unauthenticated |

The reviewed organization administration screen has delegated General & settings and Members & access tabs. Its tenant settings editor changes name/contact information. Although the issue inventory mentions scoped Customers/SLA administration, the reviewed source has no corresponding delegated leaves: standalone `/admin/customers` and `/admin/tenant-sla-settings` pages require `HelpdeskAdmin`. This change preserves their actual server gates and does not invent broader access. Empty Workspace for data-management-only users and empty Resources for manager-only users are omitted without removing any authorized leaf.

## Route and interaction behavior

`HelpdeskRouteLink` derives from the framework `NavLink` and overrides matching. The framework therefore produces the same active class and `aria-current="page"`; there is no CSS-only selection. Matching ignores query/fragment/trailing slash, compares case-insensitively and requires complete path segments.

- My Requests owns `self-service/my-requests` and its detail routes; Self Service excludes that branch.
- Integration hub owns `/admin/automation/integration` and its children. Automation Rules excludes that branch.
- Existing live aliases `/admin/integrations`, `/settings/connectivity` and `/admin/orchestration/orchestration` select Integration hub and expand Administration/Automation.
- Existing organization aliases `/tenant-administration` and `/tenant-settings` select Organizations and expand its parents; their pages retain their canonical redirect behavior.
- The existing category page was only routed at `/admin/categories0` although the menu linked `/admin/categories`. The canonical menu route is added to the same authorized page; the legacy alias remains and selects Ticket Categories. Tenant branding at `/admin/tenants/{tenantId}/branding` selects Branding & Identity using a single variable path segment, without selecting unrelated tenant paths.
- Account security pages `/account/authenticator` and `/account/change-password` select the account name leaf.
- Account pairing approval beneath `/account/integration-credentials/approve` selects the account Integration credentials leaf, independently from administrator Integration hub.
- Other detail/create/edit routes retain their owning leaf. Similar strings such as `/incidents-old` do not match.

Each `HelpdeskNavGroup` opens for a new matching destination and preserves user collapse on ordinary rerenders, query/fragment changes and updates to unrelated groups. It removes its location subscription on disposal. Current authorization continues to control rendering; no business/API authorization is replaced by navigation visibility.

Styled links retain MudBlazor's drawer navigation receiver and nested-group tab order. The existing responsive overlay and click dismissal remain. A bounded document listener handles Escape while the mobile overlay is visible and restores toggle focus after dismissal. Link navigation retains normal page focus. The listener/observer are removed when the layout disposes. Theme-first-paint and the existing topbar/mobile theme/search/account controls remain.

## Verification

Focused rendered component tests are in `tests/Helpdesk.Tests/NewWeb/NavMenuRenderedTests.cs` using bUnit 2.11.3 in the existing owning test project. They check the full inventory, scoped/self-service/delegated roles, empty groups, actual framework anchors/current attributes, aliases/detail routes, deep-link expansion, stable collapse, authorization changes and real link delivery to the drawer receiver.

An initial integrated Release solution build completed with zero errors. The first focused run executed 59 tests: 58 passed and the authorization-change case exposed the bUnit fake authorization service's notification ordering. The fixture now uses the production ASP.NET authorization core with bUnit's claims provider. The normal PR/main .NET validation gate records final owning-project results in its TRX artifacts; the earlier failure remains part of the local review history.

The bounded browser review passed all eight before/after renders and produced 16 captures: each render has a top-of-drawer image and a scrolled Account image. The before render uses the pinned baseline components, theme and CSS; the after render uses the actual integrated components, theme, JavaScript and compiled scoped CSS. A disposable host supplies synthetic identity/branding/API responses and a blank workspace. These captures demonstrate navigation rendering and interaction, not acceptance in an owner's deployed browser or backend.

| Evidence | Before | After |
| --- | --- | --- |
| Desktop light, 1366 × 768 | [Drawer](assets/navigation-153/before-desktop-light.png), [Account](assets/navigation-153/before-desktop-light-account.png) | [Drawer](assets/navigation-153/after-desktop-light.png), [Account](assets/navigation-153/after-desktop-light-account.png) |
| Desktop dark, 1366 × 768 | [Drawer](assets/navigation-153/before-desktop-dark.png), [Account](assets/navigation-153/before-desktop-dark-account.png) | [Drawer](assets/navigation-153/after-desktop-dark.png), [Account](assets/navigation-153/after-desktop-dark-account.png) |
| Narrow dark, 390 × 844, coarse pointer | [Drawer](assets/navigation-153/before-narrow-dark.png), [Account](assets/navigation-153/before-narrow-dark-account.png) | [Drawer](assets/navigation-153/after-narrow-dark.png), [Account](assets/navigation-153/after-narrow-dark-account.png) |
| 200% zoom-equivalent light | [Drawer](assets/navigation-153/before-zoom-200-light.png), [Account](assets/navigation-153/before-zoom-200-light-account.png) | [Drawer](assets/navigation-153/after-zoom-200-light.png), [Account](assets/navigation-153/after-zoom-200-light-account.png) |

The zoom-equivalent case uses a 683 × 384 CSS viewport with device scale factor 2 to reproduce the available CSS space and output size of a 1366 × 768 display at 200%. Native browser zoom and operating-system text scaling were not exercised. The same representative groups are expanded in every capture; this is a bounded matrix, not every role/group/theme combination.

| Measured behavior | Pinned baseline | Integrated navigation |
| --- | --- | --- |
| Drawer width | 272 px | 240 px |
| Desktop Home row | 42 px | 36 px |
| Coarse-pointer Home row | 42 px | 44 px |
| Instance-admin leaf anchors | 32 | 33: all prior destinations plus account Integration credentials |
| Current leaf at Integration hub | Integration hub and Automation Rules | Integration hub only |
| Horizontal workspace overflow | 0 px in all four cases | 0 px in all four cases |
| Synthetic long account identifier | Clips | Wraps without visible text overflow |

The synthetic account identifier is deliberately long and unbroken. Its readable wrapping, the added credential link and section captions increase the expanded desktop scroll height from 1029 to 1138 pixels, even though ordinary rows are shorter. The Account captures show the account links and Logout remain reachable through the drawer's vertical scroll; the coarse-pointer case has a 1290-pixel scroll height.

Three mobile interaction checks passed: Escape closes the visible overlay and restores the toggle's focus; clicking the backdrop closes it and restores focus; following a real navigation link dismisses it. Desktop checks also observed a 2-pixel solid keyboard focus indicator and an active stripe following a changed theme primary color. Exact inventories, dimensions and interaction results are recorded in [metrics.json](assets/navigation-153/metrics.json). Owner comparison against a running installation remains pending.
