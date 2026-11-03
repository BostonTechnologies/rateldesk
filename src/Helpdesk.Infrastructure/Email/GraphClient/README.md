# Bounded Microsoft Graph mail client

This directory replaces the full Microsoft.Graph 6.8.0 assembly with Kiota source
for the eleven operations used by RatelDesk's existing Graph mailbox adapter,
sender, processor and email service. Normal builds compile the checked-in source;
they do not download metadata or run a generator. Microsoft.Graph.Core 4.0.1 and
the existing Kiota 2.1.2 runtime family continue to own HTTP, authentication,
serialization, retries and paging. Application mail flows are unchanged.

`Generated/` is produced by Kiota **1.35.0**, the generator version recorded by
the upstream 6.8.0 SDK. The public v1.0 metadata is pinned to commit
`29d027ab701b8d16c5fafc8cee31bb22d29eecad` and SHA-256
`69de68b2c895bc608a58229e662e2b3eeb23524385516afc250ff0b8f165ff29`.
This public description is not claimed to be byte-identical to the upstream
SDK's internal description. All 87 included parsable model/enum contracts were
compared with 6.8.0: property types, wire field names, enum values and default
OData types matched. The native acceptance gate repeats that comparison and the
existing mail fixtures; compilation checks every application call site.

`GraphServiceClient.cs` and `CustomBatchRequestBuilder.cs` derive from SDK commit
`8139c5d736164db753cf97cd557a1cedab18fd25`. Adaptations add nullable annotations
and retain the upstream `6.8.0` SDK telemetry identity independently of the owning
product assembly version. Constructors, adapters, authentication, batching and
disposal remain the upstream implementations. `LICENSE.txt` is the SDK's MIT
license and is included in API publish output. `generation-receipt.json` records
the generated file hashes and provenance.

From the repository root, using the SDK selected by `global.json`:

```bash
python3 tools/size-campaign/generate-graph-mail.py \
  --work-directory /tmp/rateldesk-graph-check --check
```

The work directory must be fresh. Omit `--check` to regenerate. Do not hand edit
generated files. When adding a Graph operation, add its explicit method/path to
the generator, regenerate, refresh the receipt, inspect the contract comparison,
and run all Graph fixtures plus native API fresh/upgrade acceptance for both
providers and architectures. Keep delta paging, raw MIME, attachments, move,
folder creation and sendMail paths together. Metadata, SDK Core and Kiota updates
require the same review; unrelated Microsoft Graph services are not silently
included. Adding this bounded client does not establish trim or AOT compatibility.
