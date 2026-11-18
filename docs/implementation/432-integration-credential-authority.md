# Integration credential authorization acceptance

[Issue #432](https://github.com/BostonTechnologies/RatelDesk/issues/432) · [PR #438](https://github.com/BostonTechnologies/RatelDesk/pull/438)

Chromium exercised signed-in local accounts against disposable Web, API and PostgreSQL. The baseline was the scheduled-report branch at `0.1.4-beta.1`. The candidate identified itself as `0.1.4-beta.2`. The two tenants and all accounts were synthetic.

| Scenario | Observed result | Capture |
| --- | --- | --- |
| Self-service baseline | The account navigation showed **Integration credentials** and the page offered client review and credential creation. | [Baseline](assets/432-integration-credential-authority/basic-before.png) |
| Self-service candidate | The navigation link disappeared. The credential, approval and administration-alias URLs all reached **Access denied**. | [Denied](assets/432-integration-credential-authority/basic-after.png) |
| Tenant administrator with Incident Reader in another tenant | The credential wizard offered the one administered organization. The second tenant was absent from the chooser and pairing review. | [Tenant scope](assets/432-integration-credential-authority/tenant-after.png) |
| Instance administrator | The Integration hub retained credential, host review and system connection controls. An ordinary credential could be created in the second tenant. | [Instance access](assets/432-integration-credential-authority/instance-after.png) |
| Tenant admin grant removed | The old API session returned HTTP 401. A new session returned HTTP 403 and the browser denied direct approval. The pending approved device request returned `denied` with no credential. | [Role removed](assets/432-integration-credential-authority/removed-after.png) |

Direct API calls as the basic account returned HTTP 403 for listing, creating, revoking, setup options, setup creation, host review and client review, approval and denial. The mixed account also received HTTP 403 when creating or approving in the second tenant. Its list stayed empty after the denied calls, and the same pending device request remained available for the authorized tenant administrator to approve. In the authorized tenant, creation returned HTTP 201 and pairing approval returned HTTP 204. [Sanitized response receipt](assets/432-integration-credential-authority/receipt.json)

The focused regressions cover owner-record filtering across tenants, rejection of stale browser role claims, and revalidation before device delivery and acknowledgement.
