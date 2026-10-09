# Connect NetRatel and RatelDesk

Open **Account → Integration credentials → System connections** at
`/account/integration-credentials` in either product. API & MCP credentials
remain in their separate tab.

1. On either product, select **Generate a Pairing Code**. Copy the displayed
   code to the other product. It expires after five minutes, is single use,
   and a newly generated code immediately replaces the previous code.
2. On the other product, select **Create Connection**. Enter the peer's
   **Address** and **Pairing code**, then select **Pair & connect**. Supported
   addresses include DNS names and IP addresses with HTTP/HTTPS and a port.
   The exchange runs on the server and stays on this page.
3. After **Systems paired**, select the real **NetRatel tenant** and
   **RatelDesk organization**. Choose that organization's customer when
   enabling incident creation. Enter a connection name.
4. Select the independent capabilities you need: **Create incidents**
   (NetRatel → RatelDesk), **Run automation** (RatelDesk → NetRatel), or both.
   Automation is never enabled implicitly.
5. Select **Save** once. It validates the current permissions and selected
   resources, provisions the connector or automation binding, and activates
   the connection. **Connected** describes a saved and authorized mapping.

Pairing alone permits setup and authorized directory choices. It does not
permit incident creation or automation. Only existing authorized tenants,
organizations and customers are selectable; pairing creates none of them.
There is no consent page, second sign-in, approval checkbox, enable switch or
mandatory test. If Save fails, correct the actionable inline error and retry
the same form. Refresh reopens the existing unconfigured pairing.

## Manage a connection

The list shows the connection name, peer address, mapped tenants, enabled
directions, status and last test result/time. **View** opens its configuration.
**Test connection** checks authenticated access, the current mapping and
selected capabilities without creating an incident or task. Testing is
optional after Save.

**Delete connection** removes the selected mapping and revokes its local
authority immediately, including when the peer is unavailable. Other mappings
between the same installations remain usable. Deleting the last mapping
also removes the pair credentials. An unused pairing can be deleted. Any
peer cleanup runs automatically; an offline peer has not acknowledged it.
Existing incidents, receipts, Flow runs and task history remain intact.

## Addresses and credentials

Valid HTTPS works with public or private DNS and reverse proxies. Deliberately
entered private HTTP hostnames or IPs use the same two-field form. Normal TLS
certificate and hostname verification remain enabled. The server checks DNS
destinations at connection time, blocks reserved and metadata destinations,
uses fixed protocol routes, refuses redirects and bounds request time and
response size. Separate Web/API origins are resolved through verified peer
metadata; there is no additional API-address input or private-HTTP switch.

Codes and credentials belong outside URLs, logs, notifications and browser
storage. Long-lived credentials stay protected on the server. The pairing
retains each administrator's setup authority and rechecks current permissions
when selecting resources, saving and using a business capability.

## Upgrade from the previous setup

Upgrade both products before pairing. Forward migrations retire previous
NetRatel ↔ RatelDesk setups, revoke their credentials and disable their old
connector/provider bindings. Pair afresh and save each desired mapping using
the steps above. Old permissions are not transferred to new connections.
No manual SQL cleanup, installation identity reset or cleanup wizard is needed.
Mixed-version use of the discarded protocol is unsupported.

Installation and Flow producer identities, signing keys, unrelated API/MCP
credentials, Netclaw connections and business history are preserved. Keep the
normal database and persistent-key backups when upgrading.

After installation, check pairing from either product, Save the intended
mapping, optionally Test, exercise each desired business capability, and
Delete a disposable connection. Published artifact integrity and source-pair
validation are separate from acceptance of your deployed installation.
