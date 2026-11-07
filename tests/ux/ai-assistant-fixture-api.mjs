// Loopback-only deterministic API fixture for real NewWeb presentation routes.
// This is browser presentation evidence, not live Netclaw/AiAssistant or authorization evidence.
import http from 'node:http';
const conversation = '11111111-1111-4111-8111-111111111111';
const actor = 'c0c48409df3ca07e12725a961d4152790fd135c2384a3e3a522282e2bd847178';
let events = [], state = 0, recoveryResult = null;
let streamOpens = 0, sends = 0, recoveries = 0;
let starts = 0, startAttempts = 0, activeConversation = conversation, loseStartResponse = false, connectionMode = 'verified';
let holdStartResponse = false;
const heldStarts = [];
const startRequests = new Map();
const generalChats = new Map();
const generalRow = (id = conversation, organizationId = 'org', title = 'Synthetic organization planning') => ({ conversationId: id, organizationId, title, guidanceSnapshot: 'Pinned synthetic organization guidance', state });
const investigation = () => ({ id: conversation, organizationId: 'org', title: 'Synthetic ticket investigation', ticketId: 'ux-chat', ticketType: 'incidents', contextSnapshot: 'Pinned synthetic ticket and guidance', createdUtc: new Date().toISOString(), state });
let netclawPresentationMode = 'first-run';
let netclawPairResponseStatuses = [];
const streams = new Set();
const tracePayloads = {
  skillArguments: JSON.stringify({ Name: 'example-skill', _rationale: 'Inspect synthetic authentication and token usage' }, null, 2),
  skillResult: '# Example skill\nFull instructions with `code`, emoji 😀 and <script>window.traceExecuted = true</script>.',
  discoveryArguments: '{"query":"synthetic tools"}',
  discoveryResult: JSON.stringify({ tools: Array.from({ length: 300 }, (_, i) => ({ name: `example_tool_${i}`, description: 'Synthetic discovery result retained in full' })) })
};
const diskTable = '| Host | Total | Free | Sample time (UTC) |\n| :--- | ---: | ---: | :---: |\n| node-01 | 39.2 GiB | **17.8 GiB** | `16:40:36` |\n| node-02-with-a-long-synthetic-host-name.example.test | 128 GiB | | `a\\|b` |';
const emptyTable = 'Source | Rows\n--- | ---:\nEmpty synthetic dataset | 0';
const tableReply = `# Capacity report\n\n${diskTable}\n\nOnly the selected table is copied.\n\n${emptyTable}\n\nRaw output stays inert: <img src=x onerror=alert(1)>\n\n\`\`\`markdown\n| Example |\n| --- |\n| fenced table |\n\`\`\``;
function event(type, text, extra = {}) {
  const value = { Id: crypto.randomUUID(), ConversationId: activeConversation, Sequence: events.length + 1,
    Type: type, Text: text, CreatedUtc: new Date().toISOString(), ...extra };
  events.push(value);
  for (const stream of streams) stream.write(`id: ${value.Sequence}\nevent: chat\ndata: ${JSON.stringify(value)}\n\n`);
  return value;
}
function completeChildTurn(turnNumber, children, outcome = 'completed', quiet = false) {
  const OperatorMessageId = events.findLast(item => item.Type === 'operator').ClientMessageId;
  event('turn_completed', 'Native turn completed.', { MetadataJson: JSON.stringify({
    Version: 1, OperatorMessageId, TurnNumber: turnNumber, TurnOutcome: outcome, QuietEnd: quiet,
    OpenChildRunIds: children, ChildSnapshotKnown: true,
    ObservationExpiresAtUtc: new Date(Date.now() + 3600000).toISOString()
  }) });
  state = children.length ? 5 : 0;
  recoveryResult = children.length ? `Waiting for ${children.length} delegated follow-ups on the original request.` : null;
  for (const stream of streams) {
    stream.write(`event: state\ndata: ${state}\n\n`);
    stream.write(`event: recovery\ndata: ${JSON.stringify({ RecoveryResult: recoveryResult, Events: [] })}\n\n`);
  }
}
function reset(mode = 'completed') {
  activeConversation = conversation; starts = startAttempts = 0; loseStartResponse = false; startRequests.clear(); connectionMode = 'verified';
  generalChats.clear(); generalChats.set(conversation, generalRow()); streamOpens = sends = recoveries = 0;
  events = []; state = ['processing', 'quiet'].includes(mode) ? 1 : mode === 'approval' ? 2 : mode === 'archived' ? 4 : mode === 'recovery' ? 3 : 0;
  recoveryResult = null;
  event('created', 'Conversation created.');
  if (mode === 'empty') return;
  event('operator', 'Investigate the Helpdesk integration and explain the result.', { CreatedByUserId: actor, ClientMessageId: crypto.randomUUID() });
  for (let i = 0; i < 14; i++) {
    const metadata = { DisplayName: i === 12 ? 'terminal_execute_12_with_a_long_tool_identifier_that_must_wrap_inside_the_activity_panel'
      : `terminal_execute_${i}`, ToolKind: 'mcp', ProviderName: 'Example Provider', Outcome: 'running' };
    if (mode === 'payloads' && i < 2) {
      Object.assign(metadata, { DisplayName: i === 0 ? 'example-skill' : 'Discover tools', ToolKind: i === 0 ? 'skill' : 'discovery', ProviderName: null,
        Details: { Version: 2, ToolName: i === 0 ? 'skill_load' : 'search_tools', SourceTimestampUtc: '2026-11-03T08:00:00Z',
          Arguments: i === 0 ? tracePayloads.skillArguments : tracePayloads.discoveryArguments } });
    }
    event('tool_call', 'tool: started' , { CallId: `call-${i}`, MetadataJson: JSON.stringify(metadata) });
    if (mode !== 'processing' || i < 13) event('tool_result', 'tool: finished', { CallId: `call-${i}`,
      MetadataJson: JSON.stringify({ ...metadata, Outcome: mode === 'failed' && i === 13 ? 'failed' : 'completed', DurationMs: 1851,
        ...(mode === 'payloads' && i < 2 ? { Details: { Version: 2, ToolName: metadata.Details.ToolName, SourceTimestampUtc: '2026-11-03T08:00:01Z', Result: i === 0 ? tracePayloads.skillResult : tracePayloads.discoveryResult } } : {}) }) });
  }
  if (mode === 'approval') event('approval_request', 'Allow this tool operation?', { CallId: 'approval-1', OptionsJson: JSON.stringify([{ Key: 'approve_once', Label: 'Allow once' }, { Key: 'deny', Label: 'Deny' }]) });
  if (mode === 'quiet') {
    recoveryResult = 'Waiting for NetClaw with no recent model or tool output. RatelDesk keeps observing the original request. An available connection does not prove completion.';
    event('observation_waiting', recoveryResult);
  }
  if (['children', 'children-failed', 'children-quiet'].includes(mode)) {
    if (mode === 'children') event('assistant', 'First parent reply; delegated follow-ups are pending.');
    completeChildTurn(1, ['synthetic-child-a', 'synthetic-child-b'], mode === 'children-failed' ? 'failed' : 'completed', mode === 'children-quiet');
  } else if (!['processing', 'quiet', 'approval', 'recovery'].includes(mode)) {
    event('assistant', mode === 'tables' ? tableReply : 'The integration is responding normally.\n\n• Ticket context reached the harness.\n• Tool activity was recorded safely.\n\nNo production changes were made.');
    event('turn_completed', 'Turn completed.');
  }
}
reset();
function connectionStatus(organizationId, selected) {
  const now = new Date().toISOString();
  return { organizationId, enabled: true, endpoint: 'https://netclaw.example.test', source: 'database', revision: 3,
    lastTestedAtUtc: now, lastTestSucceeded: true, observedAtUtc: connectionMode === 'stale' ? new Date(Date.now() - 120000).toISOString() : now,
    observationAvailable: true, setupUrl: '/admin/automation/integration/netclaw',
    sessions: ['connected', 'stale'].includes(connectionMode) && organizationId === 'org'
      ? [{ conversationId: selected || activeConversation, sessionId: 'signalr/synthetic-session', state: 1, authenticatedAtUtc: now, lastActivityUtc: now }] : [] };
}
http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  let chatPath = url.pathname;
  const reply = value => { res.setHeader('Content-Type', 'application/json'); res.end(JSON.stringify(value)); };
  if (url.pathname === '/fixture/trace-payloads') return reply(tracePayloads);
  if (url.pathname === '/fixture/table-payloads') return reply({ diskTable, emptyTable, tableReply });
  if (url.pathname === '/fixture/chat-counts') return reply({ streamOpens, sends, recoveries, starts, startAttempts });
  if (url.pathname === '/fixture/release-start') { for (const release of heldStarts.splice(0)) release(); return reply({ ok: true }); }
  if (url.pathname === '/fixture/connection') { connectionMode = url.searchParams.get('mode') || 'verified'; return reply({ mode: connectionMode }); }
  if (url.pathname.endsWith('/chat/connection')) return reply(connectionStatus('org', url.searchParams.get('conversationId')));
  if (url.pathname === '/api/v1/ai/chat/organizations' || url.pathname === '/api/v1/ai/ops/organizations') return reply([{ id: 'org', name: 'Synthetic organization' }, { id: 'org-b', name: 'Other synthetic organization' }]);
  if (url.pathname === '/api/v1/ai/ops/prompts') return reply([]);
  if (url.pathname === '/api/v1/ai/ops/investigations') return reply([investigation()]);
  if (url.pathname === `/api/v1/ai/ops/investigations/${conversation}`) return reply(investigation());
  if (url.pathname === `/api/v1/ai/ops/investigations/${conversation}/archive`) { state = 4; for (const stream of streams) stream.write('event: state\ndata: 4\n\n'); return reply({}); }
  const orgRoute = url.pathname.match(/^\/api\/v1\/ai\/chat\/organizations\/([^/]+)(.*)$/);
  if (orgRoute) {
    const [, org, suffix] = orgRoute;
    if (suffix === '/capabilities') return reply({ enabled: true, canWrite: true, setupUrl: '/admin/automation/integration/netclaw' });
    if (suffix === '/connection') return reply(connectionStatus(org, url.searchParams.get('conversationId')));
    if (suffix.startsWith('/conversations/')) {
      const row = generalChats.get(suffix.split('/')[2]);
      if (!row || row.organizationId !== org) { res.statusCode = 404; return reply({}); }
      return reply({ ...row, state });
    }
    if (suffix === '/history') return reply([...generalChats.values()].filter(x => x.organizationId === org && x.title.toLowerCase().includes((url.searchParams.get('q') || '').toLowerCase()) && (url.searchParams.get('archived') !== 'false' || state !== 4)).slice(Number(url.searchParams.get('skip') || 0), Number(url.searchParams.get('skip') || 0) + 50).map(x => ({ ...x, state, createdByUserId: actor, lastActivityUtc: new Date().toISOString() })));
    if (suffix === '/new') {
      let body = ''; for await (const part of req) body += part;
      const request = JSON.parse(body); const row = generalRow(request.conversationId, org, request.title); generalChats.set(row.conversationId, row); return reply(row);
    }
    if (suffix === '/start') {
      let body = ''; for await (const part of req) body += part;
      const request = JSON.parse(body); startAttempts++;
      if (startRequests.has(request.conversationId)) {
        if (startRequests.get(request.conversationId) !== body) { res.statusCode = 409; return reply({}); }
        return reply(generalChats.get(request.conversationId));
      }
      starts++; sends++; activeConversation = request.conversationId; events = []; state = 0;
      const normalized = request.text.trim().replace(/\s+/g, ' ');
      const row = generalRow(request.conversationId, org, [...normalized].slice(0, 80).join('') + ([...normalized].length > 80 ? '…' : ''));
      generalChats.set(row.conversationId, row); startRequests.set(row.conversationId, body);
      event('created', 'Organization conversation started.');
      event('operator', request.text, { CreatedByUserId: actor, ClientMessageId: request.clientMessageId });
      event('assistant', 'Synthetic first reply.'); event('turn_completed', 'Turn completed.');
      if (holdStartResponse) { holdStartResponse = false; await new Promise(resolve => heldStarts.push(resolve)); if (res.destroyed) return; }
      if (loseStartResponse) { loseStartResponse = false; req.socket.destroy(); return; }
      return reply(row);
    }
    if (suffix === '/archive' || suffix === '/abandon') { state = 4; event('archived', 'Archived synthetic conversation.'); for (const stream of streams) stream.write('event: state\ndata: 4\n\n'); return reply({}); }
    chatPath = `/chat${suffix}`;
    if (suffix === '') return reply({ conversationId: url.searchParams.get('conversationId'), state, lastSequence: events.length, events, recoveryResult });
  }
  if (chatPath.endsWith('/chat/reconcile')) {
    recoveries++;
    for (const stream of streams) stream.write(`event: recovery\ndata: ${JSON.stringify({ ConversationId: conversation, State: state, LastSequence: events.length, Events: [], RecoveryLastAttemptUtc: new Date().toISOString(), RecoveryResult: 'Admission confirmed; outcome unknown. Saved history does not provide an exact-message terminal outcome. No message was resent.' })}\n\n`);
    return reply({});
  }
  if (chatPath.includes('/chat/interactions/')) {
    event('approval_response', 'approve_once', { CallId: 'approval-1' }); state = 1;
    for (const stream of streams) stream.write('event: state\ndata: 1\n\n'); return reply({});
  }
  if (url.pathname === '/fixture/netclaw') {
    const requestedMode = url.searchParams.get('mode');
    if (['first-run', 'legacy', 'connected', 'uncertain'].includes(requestedMode)) {
      netclawPresentationMode = requestedMode;
      netclawPairResponseStatuses = [];
    }
    return reply({ mode: netclawPresentationMode, pairResponseStatuses: [...netclawPairResponseStatuses] });
  }
  if (url.pathname === '/fixture/reset') { reset(url.searchParams.get('mode')); loseStartResponse = url.searchParams.get('loseStartResponse') === 'true'; holdStartResponse = url.searchParams.get('holdStartResponse') === 'true'; return reply({ ok: true }); }
  if (url.pathname === '/fixture/draft') {
    const table = url.searchParams.get('table');
    const text = table === 'partial' ? '| Host | Total | Free | Sample time (UTC) |\n| :--- | ---:' : table === 'complete' ? tableReply : 'Streaming response';
    for (const stream of streams) stream.write(`event: text-delta\ndata: ${JSON.stringify({ ConversationId: conversation, Text: text, AfterSequence: events.length })}\n\n`);
    return reply({ ok: true });
  }
  if (url.pathname === '/fixture/complete') {
    event('assistant', url.searchParams.get('table') === 'true' ? tableReply : 'Canonical response'); event('turn_completed', 'Turn completed.'); state = 0;
    for (const stream of streams) stream.write('event: state\ndata: 0\n\n');
    return reply({ ok: true });
  }
  if (url.pathname === '/fixture/child-followup') {
    const final = url.searchParams.get('final') === 'true';
    event('assistant', 'Repeated valid delegated report.');
    completeChildTurn(final ? 3 : 2, final ? [] : ['synthetic-child-b']);
    return reply({ ok: true });
  }
  if (url.pathname === '/fixture/replay') {
    for (const stream of streams) for (const item of events)
      stream.write(`id: ${item.Sequence}\nevent: chat\ndata: ${JSON.stringify(item)}\n\n`);
    return reply({ ok: true });
  }
  if (url.pathname.endsWith('/system/token')) {
    const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
    return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: actor, auth_mode: 'development', preferred_username: 'UX Test Operator', roles: ['HelpdeskAdmin', 'Incident.User'], exp: Math.floor(Date.now()/1000)+3600 })}.fixture` });
  }
  if (url.pathname === '/api/v1/auth/me') return reply({
    isAuthenticated: true, name: 'UX Test Operator', email: 'UX Test Operator', userId: actor,
    primaryOrganizationId: null, primaryOrganizationName: null, customerId: null,
    isHelpdeskAdmin: true, roleBundles: ['HelpdeskAdmin', 'Incident.User'], permissions: ['HelpdeskAdmin', 'Incident.User'],
    allowedOrganizationIds: [], managedOrganizationIds: [], scopedPermissionGrants: []
  });
  if (url.pathname === '/api/v1/branding') return reply({ applicationName: 'RatelDesk', faviconUrl: '/favicon.ico' });
  if (req.method === 'GET' && url.pathname === '/api/v1/admin/netclaw') {
    const connected = netclawPresentationMode === 'connected';
    const verificationFailed = netclawPresentationMode === 'uncertain-saved';
    return reply({
      providerKey: 'Netclaw', enabled: connected || verificationFailed, runtimeSupported: true, runtimeIssue: null,
      instance: 'dev', endpoint: connected || verificationFailed ? 'http://192.168.1.20:5199/hub/session' : null,
      daemonAddress: connected || verificationFailed ? 'http://192.168.1.20:5199' : null, allowPrivateHttp: false,
      idleMinutes: 15, connectionCapacity: 25, turnInactivityTimeout: '00:05:00',
      activityHeartbeatInterval: '00:00:15', updatedAtUtc: null,
      lastAppliedAtUtc: connected || verificationFailed ? '2026-09-28T08:00:00Z' : null,
      lastTestedAtUtc: connected ? '2026-09-28T08:05:00Z' : null,
      lastTestSucceeded: connected ? true : verificationFailed ? false : null,
      revision: connected || verificationFailed ? 7 : 6, source: 'database', managedByDeployment: false,
      hasDeviceToken: connected || verificationFailed, secretUnavailable: false,
      secretState: connected || verificationFailed ? 'configured' : 'not-configured',
      sourceKey: null, profileFingerprint: 'fixture-profile'
    });
  }
  if (req.method === 'GET' && url.pathname === '/api/v1/admin/netclaw/legacy-sessions/unbound') return reply([]);
  if (req.method === 'POST' && url.pathname === '/api/v1/admin/netclaw/pair-and-save') {
    if (netclawPresentationMode === 'legacy') {
      netclawPresentationMode = 'legacy-reviewed';
      res.statusCode = 409;
      netclawPairResponseStatuses.push(res.statusCode);
      return reply({
        code: 'legacy_ownership_confirmation_required',
        message: 'Fixture-only untrusted detail must not be rendered.',
        canonicalEndpoint: 'http://192.168.1.20:5199/hub/session',
        daemonAddress: 'http://192.168.1.20:5199',
        legacyConversationCount: 2,
        legacyOwnershipReviewToken: 'fixture-ownership-review-token'
      });
    }
    if (netclawPresentationMode === 'uncertain') {
      netclawPresentationMode = 'uncertain-saved';
      res.statusCode = 502;
      netclawPairResponseStatuses.push(res.statusCode);
      return reply({ code: 'pairing_saved_verification_uncertain', message: 'Fixture-only untrusted detail must not be rendered.' });
    }
    netclawPresentationMode = 'connected';
    netclawPairResponseStatuses.push(res.statusCode);
    return reply({
      providerKey: 'Netclaw', enabled: true, runtimeSupported: true, instance: 'dev',
      endpoint: 'http://192.168.1.20:5199/hub/session', daemonAddress: 'http://192.168.1.20:5199',
      idleMinutes: 15, connectionCapacity: 25, turnInactivityTimeout: '00:05:00', activityHeartbeatInterval: '00:00:15',
      lastAppliedAtUtc: '2026-09-28T08:00:00Z', lastTestedAtUtc: '2026-09-28T08:05:00Z',
      lastTestSucceeded: true, revision: 7, source: 'database', managedByDeployment: false,
      hasDeviceToken: true, secretUnavailable: false, secretState: 'configured', profileFingerprint: 'fixture-profile'
    });
  }
  if (req.method === 'POST' && url.pathname === '/api/v1/admin/netclaw/test') {
    netclawPresentationMode = 'connected';
    return reply({ success: true, statusCode: 200, message: 'Authenticated SignalR verified.', sessionProtocol: 'SignalR' });
  }
  if (chatPath.endsWith('/chat/stream')) {
    streamOpens++;
    res.writeHead(200, { 'Content-Type': 'text/event-stream' });
    for (const item of events.filter(x => x.Sequence > Number(url.searchParams.get('cursor') ?? 0))) res.write(`id: ${item.Sequence}\nevent: chat\ndata: ${JSON.stringify(item)}\n\n`);
    res.write(`event: state\ndata: ${state}\n\n`);
    streams.add(res); req.on('close', () => streams.delete(res)); return;
  }
  if (url.pathname.endsWith('/chat-capabilities')) return reply({ enabled: true, reason: null });
  if (url.pathname.endsWith('/chat/history')) return reply([{ conversationId: conversation, state, lastActivityUtc: new Date().toISOString(), createdByUserId: actor }]);
  if (chatPath.endsWith('/chat/stop-waiting')) {
    state = 3; event('delivery_unknown', 'Operator stopped waiting locally.');
    for (const stream of streams) stream.write('event: state\ndata: 3\n\n');
    return reply({ accepted: true });
  }
  if (chatPath.endsWith('/chat/messages')) {
    sends++;
    let body = ''; for await (const part of req) body += part;
    const message = JSON.parse(body);
    if (state !== 0) { res.statusCode = 409; return reply({ error: 'busy' }); }
    state = 1; event('operator', message.text, { ClientMessageId: message.clientMessageId, CreatedByUserId: actor });
    for (const stream of streams) stream.write('event: state\ndata: 1\n\n');
    return reply({ accepted: true });
  }
  if (chatPath.endsWith('/chat')) return reply({ conversationId: conversation, state, lastSequence: events.length, events, recoveryResult });
  if (url.pathname === '/api/v1/incidents/ux-chat') return reply({ id: 'ux-chat', subject: 'AiAssistant UX fixture', trackingId: 'UX-799', state: 0, priority: 1, categoryIds: [] });
  if (url.pathname.endsWith('/count')) return reply(0);
  if (url.pathname.includes('summary')) return reply({});
  if (url.pathname.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
  return reply([]);
}).listen(18299, '127.0.0.1', () => console.log('UX fixture API on loopback :18299'));
