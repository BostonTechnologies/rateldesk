// UI-only synthetic responses. Real acceptance, permissions and persistence are API/provider tests.
import { readFileSync } from 'node:fs';
const fixture = () => JSON.parse(readFileSync(new URL('./fixtures/automation-report.json', import.meta.url)));
let active = false, templates = [], relations = [];
const id = '79000000-0000-4000-8000-000000000420';
const ticket = (type) => ({ id: type + '-1', type, reference: ({ incidents: 'INC-DEMO-001', requests: 'REQ-DEMO-001', changes: 'CHG-DEMO-001' })[type], title: 'Multi-node directory test', state: type === 'changes' ? 'ImplementationInProgress' : 'New', url: '/' + type + '/' + type + '-1' });
function relation(source, target, kind = 1) {
 return { id, sourceTicketId: source.id, sourceType: source.type, sourceTrackingId: source.reference, sourceSubject: source.title, sourceState: 0, sourceLifecycleState: source.state, sourceUrl: source.url,
 targetTicketId: target.id, targetType: target.type, targetTrackingId: target.reference, targetSubject: target.title, targetState: 0, targetLifecycleState: target.state, targetUrl: target.url, relationType: kind,
 createdUtc: '2026-11-16T08:00:10Z', createdByUserName: 'Automation', isOutgoing: true };
}
export async function handleAutomationTrace(req, url, reply, body) {
 const path = url.pathname.replace(/\/$/, '');
 if (path === '/fixture/automation-trace') { active = true; templates = []; relations = [relation(ticket('incidents'), ticket('requests'), 2)]; reply({ ok: true }); return true; }
 if (path === '/fixture/reset') { active = false; templates = []; relations = []; return false; }
 if (path.startsWith('/api/v1/internal-incident-templates')) {
  const data = fixture();
  if (path.endsWith('/defaults')) reply([{ id: 'system-job-failure', name: 'Job execution failure', sourceKinds: ['job'], purpose: 'netratel-internal-incident', presentation: data.preview.presentation }]);
  else if (path.endsWith('/draft')) reply(data.draft);
  else if (path.endsWith('/preview')) reply(data.preview);
  else if (path.endsWith('/publish')) { templates[0].publishedRevision = 1; templates[0].version++; reply(data.preview.presentation); }
  else if (path.endsWith('/retire')) { const state = await body(); templates[0].retired = state.retired; templates[0].version++; reply(templates[0]); }
  else if (req.method === 'POST') { const draft = await body(); templates = [{ ...draft, id, version: (draft.version ?? 0) + 1 }]; reply(templates[0]); }
  else reply({ items: templates, totalCount: templates.length });
  return true;
 }
 const reports = path.match(/^\/api\/v1\/tickets\/([^/]+)\/automation-reports$/);
 if (reports) { reply(active ? [{ incidentId: 'incidents-1', incidentReference: 'INC-DEMO-001', sourceRequestId: 'requests-1', html: fixture().preview.html, plainText: fixture().preview.plainText, presentation: fixture().preview.presentation, createdAtUtc: '2026-11-16T08:00:10Z' }] : []); return true; }
 const links = path.match(/^\/api\/v1\/tickets\/([^/]+)\/relations(?:\/(.*))?$/);
 if (links) {
  const current = links[1];
  if (links[2] === 'choices') { const type = url.searchParams.get('type'); const q = (url.searchParams.get('search') ?? '').toLowerCase(); const items = ['incidents', 'requests', 'changes'].map(ticket).filter(x => x.id !== current && (!type || x.type === type) && (x.reference + x.title).toLowerCase().includes(q)); reply({ items, totalCount: items.length, page: 1, pageSize: 25 }); }
  else if (req.method === 'DELETE') { relations = relations.filter(x => x.id !== links[2]); reply({}); }
  else if (req.method === 'POST') { const input = await body(); const source = ticket(current.split('-')[0]); const target = ticket(input.targetTicketId.split('-')[0]); const row = relation(source, target); row.id = '79000000-0000-4000-8000-000000000422'; relations.push(row); reply(row); }
  else { const items = relations.filter(x => x.sourceTicketId === current || x.targetTicketId === current).map(x => ({ ...x, isOutgoing: x.sourceTicketId === current })); reply({ items, totalCount: items.length, page: 1, pageSize: 25 }); }
  return true;
 }
 return false;
}
