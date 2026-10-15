// Synthetic transport for the actual Blazor list. Provider/authorization tests own SQL evidence.
import http from 'node:http';
let queries = [], fault = '';
const rows = Array.from({length: 65}, (_, i) => ({ id: `row-${i}`, title: `Synthetic ticket ${i}`, subject: `Synthetic ticket ${i}`,
  trackingId: `SYN-${String(i).padStart(3, '0')}`, description: 'Synthetic fixture', organizationId: i % 2 ? 'org-b' : 'org-a',
  customerOrgName: i % 2 ? 'Beta customer' : 'Alpha customer', customerId: 'contact', customerName: 'Contact', customerEmail: 'contact@example.test',
  requestedForUserId: 'contact', requestedForUserName: 'Contact', requestedForUserEmail: 'contact@example.test',
  state: i < 60 ? 0 : 6, lifecycleState: i < 60 ? 0 : 5, priority: i % 2 ? 0 : 2,
  assignedToId: null, implementorUserId: null, categoryIds: [], createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-10-15T00:00:00Z' }));
const options = { customer: [{id:'org-a',label:'Alpha customer'}, {id:'org-b',label:'Beta customer'}], for: [{id:'contact',label:'Contact (contact@example.test)'}], assignee: [{id:'staff',label:'Synthetic technician'}] };
http.createServer(async (req,res) => {
 const url = new URL(req.url, 'http://127.0.0.1'); const q = url.searchParams;
 const reply = (body,status=200) => {res.writeHead(status,{'Content-Type':'application/json'});res.end(JSON.stringify(body));};
 if (url.pathname === '/fixture/health') return reply({ok:true});
 if (url.pathname === '/fixture/reset') { queries=[];fault='';return reply({ok:true}); }
 if (url.pathname === '/fixture/state') return reply({queries});
 if (url.pathname === '/fixture/fault') { fault=q.get('mode');return reply({ok:true}); }
 if (url.pathname.endsWith('/system/token')) {
  const enc=v=>Buffer.from(JSON.stringify(v)).toString('base64url');
  const roles=req.headers['x-system-secret']==='filter-reader'?['Incident.Read','Request.Read','Change.Read']:['HelpdeskAdmin'];
  return reply({token:`${enc({alg:'none'})}.${enc({sub:'filter-operator',auth_mode:'development',preferred_username:'Synthetic operator',roles,allowed_organization_id:['org-a','org-b'],exp:Math.floor(Date.now()/1000)+3600})}.fixture`});
 }
 if(url.pathname==='/api/v1/auth/me') return reply({isAuthenticated:true,name:'Synthetic operator',email:'operator@example.test',isHelpdeskAdmin:true,roleBundles:['HelpdeskAdmin'],permissions:[],allowedOrganizationIds:['org-a','org-b'],managedOrganizationIds:[]});
 if(url.pathname==='/api/v1/branding') return reply({applicationName:'RatelDesk'});
 if(url.pathname==='/api/v1/ticketing/filter-options') return reply({items:(options[q.get('dimension')]??[]).filter(o=>o.label.toLowerCase().includes((q.get('q')??'').toLowerCase())),hasMore:false});
 if(url.pathname.endsWith('/assignees/current')) return reply(null);
 if(url.pathname.match(/^\/api\/v1\/(incidents|requests|changes)\/?$/)) {
  queries.push(url.pathname+url.search);
  if(fault==='error'){fault='';return reply({error:'Synthetic unavailable'},503);}
  if(q.get('q')==='slow') await new Promise(resolve=>setTimeout(resolve,1200));
  let filtered=rows.filter(r=>q.get('view')==='all'||(q.get('view')==='historic'?r.state===6:r.state!==6));
  if(q.get('q')) filtered=q.get('q')==='slow'?[rows[0]]:filtered.filter(r=>r.title.toLowerCase().includes(q.get('q').toLowerCase()));
  if(q.has('customerOrgId')) filtered=filtered.filter(r=>q.getAll('customerOrgId').includes(r.organizationId));
  if(q.has('forId')) filtered=filtered.filter(r=>q.getAll('forId').includes(r.customerId));
  if(q.has('assigneeId')&&!q.has('unassigned')) filtered=[];
  if(q.has('priority')) filtered=filtered.filter(r=>q.getAll('priority').includes(r.priority===2?'High':'Low'));
  if(q.has('stateValues')) filtered=filtered.filter(r=>q.getAll('stateValues').includes(r.state===6?(url.pathname.endsWith('changes')?'ImplementedSuccess':'Resolved'):(url.pathname.endsWith('changes')?'Draft':'New')));
  if(q.has('from')) filtered=filtered.filter(r=>new Date(r[q.get('timeField')==='created'?'createdAt':'updatedAt'])>=new Date(q.get('from')));
  if(q.has('to')) filtered=filtered.filter(r=>new Date(r[q.get('timeField')==='created'?'createdAt':'updatedAt'])<new Date(q.get('to')));
  filtered.sort((a,b)=>a.trackingId.localeCompare(b.trackingId)*(q.get('descending')==='false'?1:-1));
  const page=Number(q.get('page')??1),size=Number(q.get('pageSize')??25);return reply({items:filtered.slice((page-1)*size,page*size),totalCount:filtered.length,page,pageSize:size});
 }
 if(url.pathname.endsWith('/count'))return reply(0);
 if(url.pathname.includes('/stream')){res.writeHead(200,{'Content-Type':'text/event-stream'});return res.end();}
 if(url.pathname.includes('summary')||url.pathname.includes('status'))return reply({});
 return reply([]);
}).listen(18330,'127.0.0.1',()=>console.log('Ticket filter fixture on loopback :18330'));
