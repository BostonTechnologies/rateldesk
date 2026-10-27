# Beta.13 issue reconciliation

The consolidated delivery is #272. The supplied private beta.12 evidence remains
NOT_READY; source tests and release publication cannot fill its missing cases.
This table separates implemented corrections from still-required deployed proof.

| Issue | Current disposition and remaining acceptance |
| --- | --- |
| #254 | Existing request-definition contract repair remains in source. The supplied campaign has no positive create-definition operation; exact NetRatel admission and provider readback remain required. |
| #253 | Existing archived-mailbox disposal and retained-dependency conflict paths remain, with SQLite/PostgreSQL regressions. A generic organization deletion positive does not by itself prove the archived-mailbox scenario; retain exact mailbox and organization readback acceptance. |
| #252 | Existing duplicate-envelope repair has 16/16 supplied duplicate/valid HTTP and stdio controls. Retain raw transport regression tests and fresh beta.13 retest. |
| #234 | Invocation binding/terminal-order fixes remain. Actual invocation and phase-specific callback fixtures, expired identity and bounded-log proof remain incomplete. |
| #233 | Scoped notification aggregate repair remains. The supplied campaign declares a 5,000 ms prospective p95 but has no qualifying soak or completed profile-and-budget/count-correctness mapping. |
| #231 | Retry and binding repairs remain. Positive single/bulk retry obligations B6-B42/B6-B43 have no supplied cases; provider admission and independent run/callback reads are still required. |
| #230 | Own-request list/detail positives exist; B6-B44 list isolation is still unmapped. Detail isolation alone cannot close the list obligation. |
| #229 | Actual incident/request self-assignment positives exist; B6-B09/B6-B13 isolation slots remain unmapped. Preserve the positives without claiming full closure. |
| #227 | Canonical durable-mail repairs remain. The supplied obligation retains its provider CI slots but lacks the required actual failure/success/partial/uncertain/retry/cancellation and effect-binding mappings. |
| #219 | Complete campaign acceptance remains open: 67 operations NOT_RUN, incomplete domain/history/prerequisite evidence and no qualifying soak. |
| #196 | Current-source [Query caller audit](mcp-query-adapter-audit.md) documents explicit identifier filters and excluded route/unsupported selectors; execute the listed owning regressions on the PR. |
| #141 | Local sign-in label no longer contains the configured application name. Updated browser checks cover long names, narrow widths, focus and text scaling; preserve local/Hybrid/OIDC mode behavior. |
| #64 | Current persisted ticket grants follow one explicit IT support organization link. Tenant administration and unrelated provider grants do not propagate; removal revokes access. Owning regressions cover local and external identities. |
| #51 | Every parameterless PostgreSqlBuilder occurrence was replaced with the image-taking constructor, retaining the existing image versions. |

Close engineering issues only with their actual validation. Keep deployed
acceptance obligations open where the table identifies missing proof. The
[laptop continuation](../mcp-http/beta13-laptop-campaign.md) supplies the next steps;
no production scheduler, mail queue, pairing or deployment is changed here.
