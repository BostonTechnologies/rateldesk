# Ticket list queries

The incident, request and change workspace lists show tickets independently of AI
provenance. Their normal navigation, refresh and filter reset never send an
AI-only predicate. The AI Assistant, stored provenance and audit history remain
available under their existing permissions.

Compatibility inventory: the public request list accepts `aiOnly=true` for
requests with a source ticket or automation binding. The CLI `--ai-involved`
option emits `aiInvolved`; the incident/change APIs historically ignore that
parameter. These public contracts are retained. The obsolete workspace checkboxes
are removed without deleting external options or provenance.
