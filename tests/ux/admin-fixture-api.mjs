// Synthetic loopback fixture. Real authorization and provider persistence are tested separately.
import http from 'node:http';
const organizations = [{ id: 'org-1', name: 'Atlas Tenant', itSupportOrganizationId: 'provider' }, { id: 'org-2', name: 'Birch Tenant', itSupportOrganizationId: 'provider' }, { id: 'provider', name: 'Support Provider' }];
const users = [{ id: 'user-1', name: 'Synthetic Operator', email: 'operator@example.test', organizationId: 'org-1' }];
let roles, datasets, calendars, subscriptions, settings, graph, coverage, saves, reads, fault, createResults, credentials, graphTests, importStatuses, graphRows, attempts, creationOrganizations;
const clone = value => JSON.parse(JSON.stringify(value));
function reset() {
 saves = []; reads = []; attempts = []; graphTests = []; fault = ''; createResults = new Map(); credentials = new Map(); importStatuses = {}; graphRows = {}; creationOrganizations = clone(organizations);
 roles = [{ id: 'built', key: 'builtin-reader', name: 'Identical display name', scope: 1, ownerOrganizationId: null, isBuiltIn: true, isProtected: true, permissions: ['Incident.Read'], assignmentCount: 1250 },
 { id: 'custom', key: 'synthetic-custom', name: 'Identical display name', scope: 1, ownerOrganizationId: 'org-1', isBuiltIn: false, isProtected: false, permissions: ['Incident.Read', 'Request.Read'], assignmentCount: 1 }];
 datasets = ['org-1', 'org-2'].map((organizationId, i) => ({ id: `dataset-${i + 1}`, organizationId, name: i ? 'Birch inventory' : 'Atlas inventory', slug: 'inventory', sourceType: 0, keyColumn: 'id', displayColumn: 'name', searchColumns: ['name'], isActive: true, columns: [{ id: 'column-id', name: 'id', dataType: 0, isKey: true, isDisplay: false, isSearchable: false, sortOrder: 0 }, { id: 'column-name', name: 'name', dataType: 0, isKey: false, isDisplay: true, isSearchable: true, sortOrder: 1 }] }));
 calendars = [{ id: 'calendar-1', name: 'Atlas working week', tenantId: 'org-1', scopeType: 1, timeZoneId: 'Africa/Johannesburg', isActive: true, weeklyRules: [], exceptions: [] }, { id: 'system-calendar', name: 'System working week', tenantId: null, scopeType: 0, timeZoneId: 'UTC', isActive: true, weeklyRules: [], exceptions: [] }];
 settings = Object.fromEntries(organizations.map(x => [x.id, { tenantId: x.id, useBusinessHours: x.id !== 'org-2', calendarId: null }]));
 graph = Object.fromEntries(organizations.map(x => [x.id, { organizationId: x.id, tenantId: '', clientId: '', clientSecret: null, hasStoredSecret: false, enableUsers: false, enableDevices: false, enableGroups: false, enableSharePointSites: false, backgroundSyncEnabled: false, backgroundSyncCronExpression: '0 */6 * * *', hangfireEnabled: false, lastSyncStatus: 'Not run yet' }]));
 coverage = organizations.map(x => ({ id: `coverage-${x.id}`, customerOrganizationId: x.id, providerOrganizationId: 'provider', supportGroupId: 'group-1', role: 0, isEnabled: true }));
 subscriptions = [{ id: 'report-1', tenantId: 'org-1', frequency: 1, weeklyDay: 1, sendTimeLocal: '08:00:00', timeZoneId: 'Africa/Johannesburg', lookbackDays: 7, isActive: true, includeCsvAttachment: true, targets: [{ type: 0, value: 'operator@example.test' }] }];
}
reset();
http.createServer(async (req, res) => {
 const url = new URL(req.url, 'http://127.0.0.1'); const path = url.pathname.replace(/\/$/, '');
 const reply = (value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
 const body = async () => { let text = ''; for await (const chunk of req) text += chunk; return text ? JSON.parse(text) : {}; };
 if (path === '/fixture/health') return reply({ ok: true });
 if (path === '/fixture/reset') { reset(); return reply({ ok: true }); }
 if (path === '/fixture/state') return reply({ saves, reads, roles, datasets, settings, subscriptions, graph, graphTests, importStatuses, attempts, credentials: Object.fromEntries(credentials) });
 if (path === '/fixture/fault') { fault = url.searchParams.get('mode') ?? ''; if(fault==='no-datasets')datasets=[]; if(fault==='many-organizations')creationOrganizations.push(...Array.from({length:40},(_,i)=>({id:`many-org-${i}`,name:`Synthetic organization ${String(i).padStart(2,'0')}`}))); if(fault==='built-in-zero'||fault==='unknown-graph-count'){datasets.push(...[1,2,3,4].map(type=>({...clone(datasets[0]),id:`graph-${type}`,name:`Graph source ${type}`,sourceType:type})));Object.assign(graph['org-1'],{enableUsers:true,enableDevices:true,enableGroups:true,enableSharePointSites:true});} if(fault==='graph-existing'){graph['org-1']={...graph['org-1'],tenantId:'existing-tenant',clientId:'existing-client',hasStoredSecret:true,credentialRevision:'stored-revision',enableUsers:true,backgroundSyncEnabled:true,backgroundSyncCronExpression:'0 2 * * *'};} if (fault === 'no-custom') roles = roles.filter(x => x.isBuiltIn); if (fault === 'many-roles') roles.push(...Array.from({ length: 60 }, (_, i) => ({ ...roles.find(x => !x.isBuiltIn), id: `many-${i}`, key: `many-${i}`, name: `Custom role ${String(i).padStart(2, '0')}`, assignmentCount: 0 }))); return reply({ ok: true }); }
 if (path.endsWith('/system/token')) {
  const mode = req.headers['x-system-secret'];
  const permissions = mode === 'admin-fixture-reader' ? ['Incident.Read'] : mode === 'admin-fixture-delegate' ? ['Tenant.Roles.Assign', 'DataManagement.Admin'] : ['HelpdeskAdmin'];
  const encode = x => Buffer.from(JSON.stringify(x)).toString('base64url');
  return reply({ token: `${encode({ alg: 'none' })}.${encode({ sub: 'synthetic-admin', auth_mode: 'development', preferred_username: 'Synthetic Administrator', email: 'admin@example.test', organization_id: 'org-1', roles: permissions, allowed_organization_id: 'org-1', managed_organization_id: 'org-1', exp: Math.floor(Date.now() / 1000) + 3600 })}.fixture` });
 }
 const token = req.headers.authorization?.split(' ')[1];
 const permissions = token ? JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString()).roles : [];
 const admin = permissions.includes('HelpdeskAdmin');
 const scopedOrganizations = admin ? organizations : organizations.slice(0, 1);
 if (path === '/api/v1/auth/me') return reply({ isAuthenticated: true, name: 'Synthetic Administrator', email: 'admin@example.test', customerId: null, userId: 'synthetic-admin', primaryOrganizationId: 'org-1', primaryOrganizationName: 'Atlas Tenant', isHelpdeskAdmin: admin, roleBundles: permissions, permissions, allowedOrganizationIds: ['org-1'], managedOrganizationIds: admin ? [] : ['org-1'] });
 if (path === '/api/v1/branding') return reply({ applicationName: 'RatelDesk', faviconUrl: '/favicon.ico' });
 if (path === '/api/v1/ticketing/organizations/search') {
  reads.push({path,query:url.search});
  const eligible=admin?creationOrganizations:creationOrganizations.slice(0,1);
  const q=(url.searchParams.get('q')??'').toLowerCase();const id=url.searchParams.get('id');const limit=Math.min(50,Math.max(1,Number(url.searchParams.get('limit')??20)));
  const filtered=eligible.filter(item=>(!id||item.id===id)&&item.name.toLowerCase().includes(q));
  return reply({items:filtered.slice(0,limit),hasMore:filtered.length>limit,totalEligible:eligible.length});
 }
 if (req.method === 'GET') reads.push({ path, query: url.search });
 if (path === '/api/v1/resources/datasets/graph-settings/test' && req.method === 'POST') {
  const draft=await body();
  if(!admin&&draft.organizationId!=='org-1')return reply({},403);
  const {clientSecret,...safe}=draft;graphTests.push({...safe,hasDraftSecret:!!clientSecret});
  const invalid=fault==='graph-test-invalid';const denied=fault==='graph-test-denied';
  if(invalid||denied)fault='';
  const selected=[['enableUsers',1],['enableDevices',2],['enableGroups',3],['enableSharePointSites',4]].filter(([key])=>draft[key]).map(([,type])=>type);
  const sources=selected.map(sourceType=>({sourceType,succeeded:!invalid&&!(denied&&sourceType===3),outcome:invalid?2:denied&&sourceType===3?3:0,message:invalid?'Authentication failed.':denied&&sourceType===3?'Read permission is missing.':'Bounded read access confirmed.',httpStatusCode:invalid?401:denied&&sourceType===3?403:200}));
  return reply({authenticationSucceeded:!invalid,outcome:invalid?2:denied?3:0,message:invalid?'Credentials were rejected.':'Authentication checked.',sources,testToken:invalid||denied?null:'synthetic-non-secret-test-receipt',testedAtUtc:'2026-11-05T07:00:00Z'});
 }
 if (req.method !== 'GET' && path.startsWith('/api/')) {
  const payload = await body();
  const {clientSecret,...safePayload}=payload;
  attempts.push({path,method:req.method});
  if (fault === 'save') { fault = ''; return reply({ error: 'Synthetic rejected save' }, 409); }
  if (fault === 'slow-save') { fault = ''; await new Promise(resolve => setTimeout(resolve, 700)); }
  if(path==='/api/v1/resources/datasets'&&fault==='create-validation'){fault='';return reply({title:'Correct the dataset fields.',errors:{Name:['A dataset with this name already exists.']}},400);}
  saves.push({ path, method: req.method, payload: path.endsWith('/graph-settings/import') ? {...payload,connection: {...payload.connection,clientSecret:undefined}} : safePayload });
  if (path.startsWith('/api/v1/admin/role-definitions')) {
   if (req.method === 'POST') { const role = { ...payload, id: 'new-role', key: 'new-role', isBuiltIn: false, isProtected: false, scope: 1, assignmentCount: 0 }; roles.push(role); return reply(role); }
   roles = req.method === 'DELETE' ? roles.filter(x => !path.endsWith(x.id)) : roles.map(x => path.endsWith(x.id) ? { ...x, ...payload } : x); return reply({});
  }
  if (path.startsWith('/api/v1/tenant-sla-settings/')) { settings[payload.tenantId] = payload; return reply(payload); }
  if (path.startsWith('/api/v1/sla-report-subscriptions')) {
   if (req.method === 'POST') { payload.id = 'new-report'; subscriptions.push(payload); }
   else subscriptions = req.method === 'DELETE' ? subscriptions.filter(x => !path.endsWith(x.id)) : subscriptions.map(x => path.endsWith(x.id) ? payload : x);
   return reply(payload);
  }
  if (path.startsWith('/api/v1/support/coverage/')) { const row = coverage.find(x => path.endsWith(x.id)); if (row) Object.assign(row, payload); return reply(row ?? {}); }
  if(path==='/api/v1/resources/datasets/graph-settings/import') {
   const connection=payload.connection;const org=connection.organizationId;
   if(!admin&&org!=='org-1')return reply({},403);
   if(importStatuses[org]?.requestId===payload.requestId)return reply(importStatuses[org]);
   const partial=fault==='graph-import-partial';const lost=fault==='graph-import-lost';const slow=fault==='graph-import-slow';fault='';
   const before=graph[org];graph[org]={...before,...connection,clientSecret:null,hasStoredSecret:true,lastSyncStatus:partial?'PartiallySucceeded':'Succeeded',lastSyncUtc:'2026-11-05T07:01:00Z'};
   if(payload.preserveExistingSchedule){graph[org].backgroundSyncEnabled=before.backgroundSyncEnabled;graph[org].backgroundSyncCronExpression=before.backgroundSyncCronExpression;}
   const selected=[['enableUsers',1],['enableDevices',2],['enableGroups',3],['enableSharePointSites',4]].filter(([key])=>connection[key]).map(([,type])=>type);
   const sources=selected.map(sourceType=>{
    if(partial&&sourceType===3)return {sourceType,status:'Failed',rowCount:null,completedAtUtc:'2026-11-05T07:01:00Z',message:'Synthetic source unavailable.'};
    const id=`graph-${org}-${sourceType}`;let definition=datasets.find(item=>item.id===id);
    if(!definition){definition={...clone(datasets.find(item=>item.organizationId===org)??{columns:[],organizationId:org,keyColumn:'id',displayColumn:'name',searchColumns:['name'],isActive:true}),id,name:['','Graph Users','Graph Computers/devices','Graph Groups','Graph SharePoint sites'][sourceType],sourceType};datasets.push(definition);}
    const rowCount=sourceType===2?0:2;graphRows[id]=rowCount;
    const stamp=['','lastUsersSyncUtc','lastDevicesSyncUtc','lastGroupsSyncUtc','lastSharePointSitesSyncUtc'][sourceType];graph[org][stamp]='2026-11-05T07:01:00Z';
    return {sourceType,status:'Succeeded',datasetId:id,rowCount,completedAtUtc:'2026-11-05T07:01:00Z',message:null};
   });
   importStatuses[org]={requestId:payload.requestId,organizationId:org,status:partial?'PartiallySucceeded':'Succeeded',configurationSaved:true,startedAtUtc:'2026-11-05T07:00:30Z',completedAtUtc:'2026-11-05T07:01:00Z',sources,canReconcile:false};
   if(slow)await new Promise(resolve=>setTimeout(resolve,700));
   return reply(lost?{message:'Synthetic lost response after commit'}:importStatuses[org],lost?503:200);
  }
  if(path.endsWith('/reconcile')&&path.includes('/import-status/')){const status=importStatuses[path.split('/')[6]];return reply(status??{},status?200:404);}
  const credential=path.match(/^\/api\/v1\/resources\/datasets\/([^/]+)\/credentials(?:\/([^/]+))?$/);
  if(credential){
   const list=credentials.get(credential[1])??[];
   if(req.method==='POST'){
    const item={id:`key-${list.length+1}`,datasetId:credential[1],name:payload.name,isActive:true,createdAtUtc:'2026-11-05T07:00:00Z'};
    list.push(item);credentials.set(credential[1],list);return reply({credential:item,plainTextKey:'synthetic-one-time-dataset-key'});
   }
   const item=list.find(key=>key.id===credential[2]);if(item)item.isActive=false;return reply({});
  }
  if (path.endsWith('/graph-settings/org-1/sync')) { const builtIn = { ...clone(datasets[0]), id: 'graph-users', name: 'Graph Users', sourceType: 1 }; datasets.push(builtIn); return reply([builtIn]); }
 if (path === '/api/v1/resources/datasets/graph-settings') { graph[payload.organizationId] = {...graph[payload.organizationId],...payload,clientSecret:null,hasStoredSecret:graph[payload.organizationId]?.hasStoredSecret||!!clientSecret}; return reply(graph[payload.organizationId]); }
  if (/\/datasets\/[^/]+$/.test(path)) {
   const dataset = datasets.find(x => path.endsWith(x.id)); if (dataset) Object.assign(dataset, payload);
   if (req.method === 'DELETE') datasets = datasets.filter(x => !path.endsWith(x.id)); return reply(dataset ?? payload);
  }
  if (path === '/api/v1/resources/datasets') {
   const requestKey=`${payload.organizationId}:${payload.requestId}`;
   if(payload.requestId&&createResults.has(requestKey))return reply(createResults.get(requestKey));
   const lost=fault==='create-lost';const slow=fault==='create-slow';fault='';
   payload.id=`new-dataset-${datasets.length+1}`;payload.slug='synthetic';datasets.push(payload);
   if(payload.requestId)createResults.set(requestKey,clone(payload));
   if(slow)await new Promise(resolve=>setTimeout(resolve,700));
   return reply(lost?{message:'Synthetic lost response after commit'}:payload,lost?503:200);
  }
  return reply({});
 }
 if (['/api/v1/organizations', '/api/v1/ticketing/organizations', '/api/v1/tenant-admin/organizations', '/api/v1/admin/tenants/lookup'].includes(path)) return reply(scopedOrganizations);
 if (path === '/api/v1/users') return reply(users);
 if (path === '/api/v1/customers') return reply([]);
 if (path === '/api/v1/admin/role-definitions') return reply(roles);
 if (path.endsWith('/assignments')) return reply([{ id: 'assignment-1', userId: 'user-1', name: 'Synthetic Operator', email: 'operator@example.test', organizationId: 'org-1', assignedAtUtc: '2026-10-01T00:00:00Z' }]);
 if (path === '/api/v1/sla-calendars') return reply(calendars);
 if (path === '/api/v1/slas') return reply([{ id: 'policy-1', name: 'Atlas priority response', tenantId: 'org-1', appliesTo: 0, responseTimeHours: 2, resolutionTimeHours: 8, escalations: [] }]);
 if (path === '/api/v1/sla-report-subscriptions') return reply(subscriptions);
 if (path.startsWith('/api/v1/tenant-sla-settings/')) {
  const tenant = path.split('/').pop(); if (fault === 'slow-tenant' && tenant === 'org-1') await new Promise(resolve => setTimeout(resolve, 1000)); return reply(settings[tenant]);
 }
  const creation=path.match(/^\/api\/v1\/resources\/datasets\/create-requests\/([^/]+)$/);
 if(creation){const result=createResults.get(`${url.searchParams.get('organizationId')}:${creation[1]}`);return reply(result??{},result?200:404);}
 const importStatus=path.match(/^\/api\/v1\/resources\/datasets\/graph-settings\/([^/]+)\/import-status$/);
 if(importStatus){const requestId=url.searchParams.get('requestId');const latest=importStatuses[importStatus[1]];const result=latest&&(!requestId||latest.requestId===requestId)?latest:null;return reply(result??{},result?200:404);}
 if (path === '/api/v1/resources/datasets/graph-settings') {const org=url.searchParams.get('organizationId');return reply(graph[org]??{organizationId:org,hasStoredSecret:false,enableUsers:false,enableDevices:false,enableGroups:false,enableSharePointSites:false,backgroundSyncEnabled:false,backgroundSyncCronExpression:'0 */6 * * *'});}
 if (path === '/api/v1/resources/datasets') return reply(datasets.filter(x => x.organizationId === url.searchParams.get('organizationId')));
 if (path.endsWith('/rows')) {
  const query = url.searchParams.get('query') ?? ''; const page = +(url.searchParams.get('page') ?? 1); const size = +(url.searchParams.get('pageSize') ?? 50);
  if (query === 'slow') await new Promise(resolve => setTimeout(resolve, 1000));
  const datasetId=path.split('/')[5];const definition=datasets.find(item=>item.id===datasetId);
  if(fault==='unknown-graph-count'&&definition?.sourceType>0)return reply({message:'Synthetic count unavailable'},503);
  const count=definition?.sourceType>0?(graphRows[datasetId]??0):datasetId.startsWith('new-dataset-')?0:65;
  const rows = Array.from({ length: count }, (_, i) => ({ id: `row-${i + 1}`, externalKey: `SYN-${i + 1}`, data: { id: `SYN-${i + 1}`, name: `Inventory item ${i + 1}` }, lastIngestedAtUtc: '2026-10-01T10:00:00Z' })).filter(x => !query || x.data.name.toLowerCase().includes(query.toLowerCase()));
  return reply({ items: rows.slice((page - 1) * size, page * size), total: rows.length });
 }
 if (path.endsWith('/credentials')) {if(fault==='credentials-read-failure')return reply({message:'Synthetic key metadata read failed'},503);return reply(credentials.get(path.split('/')[5])??[]);}
 if (/\/datasets\/[^/]+$/.test(path)) return reply(datasets.find(x => path.endsWith(x.id)));
 if (path === '/api/v1/support/groups') return reply([{ id: 'group-1', name: 'Primary support', owningOrganizationId: 'provider', description: 'Synthetic support group', isEnabled: true }]);
 if (path === '/api/v1/support/coverage') return reply(coverage.filter(x => x.customerOrganizationId === url.searchParams.get('customerOrganizationId')));
 if (path === '/api/v1/support/notification-subscriptions') return reply([{ id: 'rule-1', customerOrganizationId: url.searchParams.get('customerOrganizationId'), eventType: 0, recipientType: 0, recipientId: 'group-1', channel: 0, isEnabled: true }]);
 if (path.endsWith('/recipient-preview')) {
  if (fault === 'slow-preview') { fault = ''; await new Promise(resolve => setTimeout(resolve, 900)); }
  return reply({ organizationId: path.split('/')[5], eventType: url.searchParams.get('eventType') === 'TicketAssigned' ? 1 : 0, recipients: [], warnings: [] });
 }
 if (path.endsWith('/count')) return reply(0);
 if (path.includes('summary') || path.includes('status')) return reply({});
 if (path.includes('/stream')) { res.writeHead(200, { 'Content-Type': 'text/event-stream' }); return res.end(); }
 return reply([]);
}).listen(18500, '127.0.0.1', () => console.log('Admin fixture listening on loopback :18500'));
