// Synthetic rendered-directory evidence. SQL and authorization are tested by the dual-provider HTTP suite.
let enabled = false, fault = '', reads = [], actions = [], organizations = [], customers = [], categories = [];
const id = index => `70000000-0000-0000-0000-${String(index).padStart(12, '0')}`;
export function resetDirectories() { enabled = false; fault = ''; reads = []; actions = []; }
function seed() {
 organizations = Array.from({ length: 137 }, (_, index) => ({ id: id(index), name: index % 7 === 0 ? `Long synthetic organization ${index} with a readable extended display name` : `Organization ${String(index).padStart(3, '0')}`, dnsName: `org${index}.example.invalid`, contactInfo: `Support contact ${index}@example.invalid`, isEnabled: index % 3 !== 0, itSupportOrganizationId: index % 2 === 0 ? id(1) : null, supportOrganizationName: index % 2 === 0 ? 'Organization 001' : null, orchestrationTenantId: index % 4 === 0 ? index + 1 : null, orchestrationTenantName: index % 4 === 0 ? `Saved automation tenant ${index + 1}` : null, orchestrationTenantLinkedAtUtc: '2026-11-01T10:00:00Z' }));
 customers = Array.from({ length: 133 }, (_, index) => ({ id: `customer-${String(index).padStart(3, '0')}`, name: index % 9 === 0 ? `Synthetic customer ${index} with an intentionally long name` : `Customer ${String(index).padStart(3, '0')}`, email: `person${String(index).padStart(3, '0')}@example.invalid`, organizationId: id(index % 137), organizationName: organizations[index % 137].name, isEnabled: index % 4 !== 0, authStatus: { customerId: `customer-${String(index).padStart(3, '0')}`, inviteStatus: index % 6, statusText: ['No linked login', 'Invitation pending', 'Login active', 'Login disabled', 'Invite failed', 'Invite expired'][index % 6], isLinkedLogin: index % 6 > 0, canInvite: index % 6 === 0, canResend: [1, 4, 5].includes(index % 6) } }));
 categories = Array.from({ length: 143 }, (_, index) => ({ id: id(index + 300), name: index % 6 === 0 ? `Long category ${index} with deliberate readable classification context` : `Category ${String(index).padStart(3, '0')}`, description: `Synthetic description ${String(index).padStart(3, '0')} — ${index % 6 === 0 ? 'A complete description that stays available without relying on a hover tooltip or tiny text.' : 'Classification summary.'}`, type: index % 4, tenantId: index % 3 === 0 ? id(index % 137) : null, tenantName: index % 3 === 0 ? organizations[index % 137].name : null, sortOrder: index % 20, isActive: index % 5 !== 0, isSystem: index % 3 !== 0, createdUtc: '2026-11-01T10:00:00Z' }));
}
export async function handleDirectoryRequest(req, res, url) {
 const path = url.pathname.replace(/\/$/, '');
 const reply = (data, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(data)); return true; };
 if (path === '/fixture/directories') { enabled = true; fault = url.searchParams.get('mode') ?? ''; seed(); if (fault === 'empty') customers = organizations = categories = []; return reply({ ok: true }); }
 if (path === '/fixture/directories/state') return reply({ reads, actions });
 if (path === '/fixture/directories/fault') { fault = url.searchParams.get('mode') ?? ''; return reply({ ok: true }); }
 if (!enabled) return false;
 const directory = path.match(/^\/api\/v1\/admin\/directories\/(customers|organizations|categories)$/)?.[1];
 if (directory && req.method === 'GET') {
  reads.push({ path, query: url.search });
  if (fault === 'error') return reply({ title: 'Synthetic recoverable directory failure' }, 503);
  let rows = structuredClone({ customers, organizations, categories }[directory]);
  const search = (url.searchParams.get('search') ?? '').toLowerCase();
  if (search === 'slow') await new Promise(resolve => setTimeout(resolve, 700));
  if (search) rows = rows.filter(row => [row.name, row.email, row.organizationName, row.dnsName, row.contactInfo, row.description].some(value => value?.toLowerCase().includes(search)));
  const organizationId = url.searchParams.get('organizationId');
  if (organizationId) rows = rows.filter(row => (directory === 'categories' ? row.tenantId : row.organizationId) === organizationId);
  for (const [key, property] of [['enabled', 'isEnabled'], ['active', 'isActive'], ['system', 'isSystem']]) if (url.searchParams.has(key)) rows = rows.filter(row => row[property] === (url.searchParams.get(key) === 'true'));
  if (url.searchParams.has('linked')) rows = rows.filter(row => (row.orchestrationTenantId != null) === (url.searchParams.get('linked') === 'true'));
  const support = url.searchParams.get('supportOrganizationId');
  if (support) rows = rows.filter(row => support === 'self' ? row.itSupportOrganizationId == null : row.itSupportOrganizationId === support);
  if (url.searchParams.has('type')) rows = rows.filter(row => row.type === Number(url.searchParams.get('type')));
  const scope = url.searchParams.get('scope'); if (scope) rows = rows.filter(row => (row.tenantId == null) === (scope === 'global'));
  const access = url.searchParams.get('access');
  if (access) rows = rows.filter(row => access === 'linked' ? row.authStatus.isLinkedLogin : access === 'unlinked' ? !row.authStatus.isLinkedLogin : row.authStatus.inviteStatus === ['not-invited', 'pending', 'active', 'disabled', 'failed', 'expired'].indexOf(access));
  const sort = url.searchParams.get('sort') ?? 'name'; const property = { organization: 'organizationName', state: 'isEnabled', access: 'inviteStatus', support: 'supportOrganizationName', orchestration: 'orchestrationTenantName', order: 'sortOrder', scope: 'tenantId', active: 'isActive', contact: 'contactInfo', dns: 'dnsName' }[sort] ?? sort;
  rows.sort((left, right) => { const a = sort === 'access' ? left.authStatus.inviteStatus : left[property]; const b = sort === 'access' ? right.authStatus.inviteStatus : right[property]; const compared = String(a ?? '').localeCompare(String(b ?? ''), undefined, { numeric: true }); return (url.searchParams.get('descending') === 'true' ? -compared : compared) || left.id.localeCompare(right.id); });
  const pageSize = Number(url.searchParams.get('pageSize') ?? 25), totalCount = rows.length;
  const page = Math.min(Number(url.searchParams.get('page') ?? 0), Math.max(0, Math.ceil(totalCount / pageSize) - 1));
  return reply({ items: rows.slice(page * pageSize, (page + 1) * pageSize), totalCount, page, pageSize });
 }
 if (path === '/api/v1/admin/directories/organizations/lookup') {
  const search = (url.searchParams.get('search') ?? '').toLowerCase(), selected = url.searchParams.get('selectedId');
  return reply(organizations.filter(row => selected ? row.id === selected : row.name.toLowerCase().includes(search)).slice(0, 50).map(({ id, name }) => ({ id, name })));
 }
 const authCustomer = path.match(/^\/api\/v1\/customers\/([^/]+)\/auth\/status$/)?.[1];
 if (authCustomer) { reads.push({ path, query: url.search }); return reply(customers.find(row => row.id === authCustomer)?.authStatus ?? {}); }
 if (path === '/api/v1/organizations') return reply(organizations);
 if (path === '/api/v1/customers' && req.method === 'GET') return reply(customers);
 if (path === '/api/v1/categories' && req.method === 'GET') return reply(categories);
 if (req.method === 'DELETE' && /^\/api\/v1\/categories\//.test(path)) { const target = path.split('/').at(-1); actions.push({ method: 'DELETE', id: target }); categories = categories.filter(row => row.id !== target); return reply(null, 204); }
 if (req.method === 'POST' && /\/customers\/[^/]+\/auth\//.test(path)) { actions.push({ method: 'POST', path }); return reply({ statusText: 'Invitation pending', inviteStatus: 1 }); }
 return false;
}
