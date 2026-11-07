# Task-first request service editor (#357)

The request service editor opens on Workflow. Desktop operators select a task in
a 280–340 pixel navigator and configure it in the adjacent inspector. Automation
stays with the selected task; Form fields and Form setup have separate tabs.
The compact title, release state, draft status and Save/Cancel remain visible.
Narrow screens switch between the task list and inspector with Back to tasks.

## Compatibility and behavior

This changes the editor presentation without changing the stored schema, routes,
authorization, organization eligibility or provider binding contracts. Task and
field identities survive selection, tab changes and ordering. Drag and keyboard
ordering preserve dependency references. Add and duplicate select the new task;
delete selects a neighboring task and removes references to the deleted task.

Validation checks the entire draft before saving, activates the relevant tab and
task, expands Advanced workflow when needed and focuses the first actionable
input. Unsaved edits remain available after validation and binding-save errors.
Cancel asks before discarding a changed draft. If the form has already saved and
binding persistence fails, retry updates that same form identity. Cancel cannot
roll back a server save that already succeeded. Existing stale-result checks,
catalog/preview actions and connection identity checks remain in place.

Current operator guidance is in
[NetRatel automation](../integrations/netratel-orchestrator.md#edit-request-workflows).

## Acceptance source and fixtures

Before screenshots use unchanged `origin/main`
`ee7067d12dafb22de81b70a58f61b211b9e37226`. After screenshots use the implementation
containing this report. Both use the same deterministic loopback fixture in
`tests/ux/request-editor-fixture-api.mjs`, the actual Blazor Web host and existing
Development authentication. All displayed organizations, identities, tasks and
automation targets are synthetic. No provider job is dispatched.

Browser assertions cover actual computed pane geometry at 1440×900, 1366×768,
1280×720 and 390×844; selection, native drag and keyboard reordering while editing;
stable dependencies; tab drafts; save/reload; hidden-tab/task validation and focus;
Cancel confirmation; empty/many tasks; dark theme and enlarged text; and a form
save followed by a deliberately rejected binding save and successful same-form
retry. The shared native-zoom suite also opens the editor at actual Chromium
200% browser zoom in both themes and checks bounded panes and reachable controls.

These deterministic receipts establish editor behavior. Existing provider/API
tests establish authorization and SQLite/PostgreSQL persistence; this report does
not claim a deployed reciprocal provider execution.

## Verification

The dedicated editor browser suite passed **7/7**, with no retries or skips, in
21.8 seconds. The shared native-zoom suite passed **4/4** in 23.1 seconds. The
focused rendered/state suite passed **36/36**, with no skips. Existing rendered
automation cases cover stale connection/catalog completions and a newly created
form whose first binding save fails, followed by a retry against the assigned ID.
The browser fault case independently covers the existing-form retry boundary.

```bash
dotnet build src/HelpDesk.NewWeb/HelpDesk.NewWeb.csproj --no-restore -m:2
dotnet test tests/Helpdesk.Tests/Helpdesk.Tests.csproj --no-restore -m:2 \
  --filter 'FullyQualifiedName~AutomationConnectionSelectionRenderedTests|FullyQualifiedName~ServiceBrowserRenderedTests|FullyQualifiedName~RequestFormTaskEditorStateTests|FullyQualifiedName~RequestFormFieldEditorStateTests'
npx playwright test --config playwright.request-editor.config.ts
HELPDESK_NATIVE_ZOOM_SUITE=request-editor \
  npx playwright test --config playwright.native-zoom.config.ts
dotnet tool run slopwatch -- analyze --fail-on warning --exclude '**/bin/**,**/obj/**'
python3 tools/ci/validate-docs.py
tools/ci/validate-layout.sh
git diff --check
```

The Web build passed and slopwatch reported zero issues. Browser before/after
captures used Debian Chromium 151.0.7922.173. Native zoom used Playwright's bundled
Chrome for Testing 156.0.8078.4, matching the CI browser setup. A preliminary
system-Chromium zoom attempt could not load the extension and was replaced before
application navigation; it is not counted as zoom evidence. The passing run
retains the actual `chrome.tabs.setZoom(2)` and layout-width assertions.

An OpenCover rerun of the same 36 focused cases passed. Coverage/risk review
identified validation branches needing direct rendered assertions; ten additional
invalid-draft cases now cover those hidden-task constraints before any write.
`IsTaskConfigurationValid` has 100% sequence coverage and 94.23% branch coverage;
`FindCircularDependencyTask` has 100% for both. Per-method complexity and coverage
are retained in the metrics file. No coverage exclusions or baselines changed.
Documentation and repository layout validation passed.

Computed geometry from the passing browser run:

| Viewport | Task navigator width | Inspector width | Save bottom | Horizontal overflow |
| --- | ---: | ---: | ---: | ---: |
| 1440×900 | 334 px | 986 px | 863 px | 0 px |
| 1366×768 | 315.5 px | 930.5 px | 734.95 px | 0 px |
| 1280×720 | 294 px | 866 px | 688.39 px | 0 px |
| 390×844 | Hidden while editing | 346 px | 826 px | 0 px |

The complete geometry, browser case names/results, native-zoom dimensions and
SHA-256 hashes of tested sources/screenshots are in
[metrics.json](assets/request-editor-357/metrics.json).

| Viewport | Before | After |
| --- | --- | --- |
| 1440×900 | [Before](assets/request-editor-357/before-1440x900.png) | [After](assets/request-editor-357/after-1440x900.png) |
| 1366×768 | [Before](assets/request-editor-357/before-1366x768.png) | [After](assets/request-editor-357/after-1366x768.png) |
| 1280×720 | [Before](assets/request-editor-357/before-1280x720.png) | [After](assets/request-editor-357/after-1280x720.png) |
| 390×844 | [Before](assets/request-editor-357/before-390x844.png) | [After](assets/request-editor-357/after-390x844.png) |

Additional captures:

- [33 tasks, dark theme, enlarged text at 1280×720](assets/request-editor-357/after-many-dark-enlarged-1280x720.png)
- [Native 200%, light, 1280 physical pixels](assets/request-editor-357/after-light-1280-native-200.png)
- [Native 200%, light, 1920 physical pixels](assets/request-editor-357/after-light-1920-native-200.png)
- [Native 200%, dark, 1280 physical pixels](assets/request-editor-357/after-dark-1280-native-200.png)
- [Native 200%, dark, 1920 physical pixels](assets/request-editor-357/after-dark-1920-native-200.png)

Independent review found no remaining issue after checking validation/focus,
duplicate/delete/reordering identity, stale-result boundaries, Cancel and partial
save/retry. Its separate focused rerun passed three critical rendered cases.
