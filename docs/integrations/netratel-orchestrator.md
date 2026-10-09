# NetRatel automation

Configure NetRatel through [System connections pairing](netratel-pairing.md).
After pairing, select the real NetRatel tenant and RatelDesk organization,
name the connection and explicitly enable **Run automation**, then **Save**.
The automation integration page uses these saved connections.

The scoped catalog, invocation and correlated callback/result paths require
current mapping authority and existing task/resource policy. A successful
token or read-only probe does not establish an accepted execution. A transport
failure after dispatch may leave an uncertain outcome; preserve its original
execution/correlation identity rather than inventing an acknowledgement or
silently submitting a second task.

Upgrade both products and pair afresh. Previous manual/deployment NetRatel
connection profiles are retired; the new pairing model is the sole setup path.
The independent Netclaw integration and API/MCP credentials are unchanged.
