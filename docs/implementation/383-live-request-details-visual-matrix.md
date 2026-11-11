# Request details: supplemental visual matrix (#383)

This supplement completes the full before/after matrix for the
[original #383 evidence](383-live-request-details.md). It adds evidence only;
the original screenshots, receipts and register remain unchanged.

Both hosts retain their frozen Web assemblies: baseline
`11c8997816833d753caf8df912a8279490da34fb` and candidate
`816046ba8c762ca5b5c09c254b1edc77ebf94afb`. The
[supplemental receipt](assets/383-live-request-details/supplemental-matrix/matrix-receipt.json)
verifies the assembly hashes against the original source receipt, records every
image hash and checks actual task-content bounds, document width and error UI.
All pairs use the same three disposable synthetic running tasks. No product,
fixture-source or shared configuration changes, rebuilds or Docker starts were
performed for these captures.

## Complete ordinary viewport matrix

| Viewport | Light baseline | Light candidate | Dark baseline | Dark candidate |
| --- | --- | --- | --- | --- |
| 1920 × 1080 | [Before](assets/383-live-request-details/supplemental-matrix/before-light-1920x1080.png) | [After](assets/383-live-request-details/supplemental-matrix/after-light-1920x1080.png) | [Before](assets/383-live-request-details/supplemental-matrix/before-dark-1920x1080.png) | [After](assets/383-live-request-details/supplemental-matrix/after-dark-1920x1080.png) |
| 1440 × 900 | [Before](assets/383-live-request-details/before-light-1440.png) | [After](assets/383-live-request-details/after-light-1440.png) | [Before](assets/383-live-request-details/before-dark-1440.png) | [After](assets/383-live-request-details/after-dark-1440.png) |
| 1366 × 768 | [Before](assets/383-live-request-details/supplemental-matrix/before-light-1366x768.png) | [After](assets/383-live-request-details/supplemental-matrix/after-light-1366x768.png) | [Before](assets/383-live-request-details/supplemental-matrix/before-dark-1366x768.png) | [After](assets/383-live-request-details/supplemental-matrix/after-dark-1366x768.png) |
| 768 × 1024 | [Before](assets/383-live-request-details/supplemental-matrix/before-light-768x1024.png) | [After](assets/383-live-request-details/supplemental-matrix/after-light-768x1024.png) | [Before](assets/383-live-request-details/supplemental-matrix/before-dark-768x1024.png) | [After](assets/383-live-request-details/supplemental-matrix/after-dark-768x1024.png) |
| 390 × 844 | [Before](assets/383-live-request-details/before-light-390.png) | [After](assets/383-live-request-details/after-light-390.png) | [Before](assets/383-live-request-details/before-dark-390.png) | [After](assets/383-live-request-details/after-dark-390.png) |

## Actual Chromium 200% zoom

The [scoped driver](assets/383-live-request-details/supplemental-matrix/capture-driver.mjs)
uses the existing isolated MV3 extension and `chrome.tabs.setZoom(tabId, 2)`.
It verifies `chrome.tabs.getZoom(tabId) === 2`, layout widths of 640/960 pixels
for physical widths of 1280/1920, and unchanged CSS zoom. CDP
`Page.captureScreenshot` retains the full physical 1280/1920 × 1000 surface
without a CSS clip. This is browser layout zoom, not a smaller viewport or a
device-scale substitute.

| Physical viewport / theme | Baseline at 200% | Candidate at 200% |
| --- | --- | --- |
| 1280 × 1000, light | [Before](assets/383-live-request-details/supplemental-matrix/before-light-1280x1000-native-200.png) | [After](assets/383-live-request-details/supplemental-matrix/after-light-1280x1000-native-200.png) |
| 1280 × 1000, dark | [Before](assets/383-live-request-details/supplemental-matrix/before-dark-1280x1000-native-200.png) | [After](assets/383-live-request-details/supplemental-matrix/after-dark-1280x1000-native-200.png) |
| 1920 × 1000, light | [Before](assets/383-live-request-details/supplemental-matrix/before-light-1920x1000-native-200.png) | [After](assets/383-live-request-details/supplemental-matrix/after-light-1920x1000-native-200.png) |
| 1920 × 1000, dark | [Before](assets/383-live-request-details/supplemental-matrix/before-dark-1920x1000-native-200.png) | [After](assets/383-live-request-details/supplemental-matrix/after-dark-1920x1000-native-200.png) |

The candidate additionally opens the technical disclosure with Enter at each
native width/theme, checks its content bounds, changes to Workflow and back to
Tasks with the keyboard, and verifies retained expansion. The receipt records
these results. Captures show the default collapsed comparison; no provider
execution or gateway acceptance is inferred from presentation fixtures.

The supplement retains twelve ordinary and eight native-zoom images. Together
with the original eight ordinary comparison frames, the required comparison
matrix contains twenty ordinary and eight native frames. The original two
additional disclosure/unknown-state figures remain separately retained.
