// Synthetic UI evidence only. Provider and authorization outcomes live in owning .NET tests.
let active = false;
let records = [];
let requests = [];
let unavailable = false;
let rejectDelete = false;
let held = [];
const organizations = Array.from({ length: 8 }, (_, index) => ({ id: `team-org-${index}`, name: `Synthetic team ${index}` }));
const json = (res, value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
export function resetTeamDirectory() {
  active = false; unavailable = false; rejectDelete = false; requests = [];
  for (const release of held) release(); held = [];
  records = Array.from({ length: 137 }, (_, index) => ({
    id: `team-user-${String(index).padStart(3, '0')}`, name: `Synthetic person ${String(index).padStart(3, '0')}${index === 0 ? ' · a long identity with readable full text' : ''}`,
    email: `person${String(index).padStart(3, '0')}@example.invalid`, role: index % 3 === 0 ? 'Technician' : 'User', roleKeys: [],
    isTestUser: index % 2 === 0, organizationId: organizations[index % 8].id, organizationName: organizations[index % 8].name,
    accountState: index % 5 === 0 ? 'Disabled' : 'Enabled', accountId: `team-user-${String(index).padStart(3, '0')}`,
    canManageAccount: true, canChangeSignIn: true, canDelete: true, deleteBlockedReason: null
  }));
}
resetTeamDirectory();
export function handleTeamDirectory(req, res, url) {
  const path = url.pathname;
  if (path === '/fixture/team') { resetTeamDirectory(); active = true; json(res, { active: true }); return true; }
  if (!active) return false;
  if (path === '/fixture/team/state') { json(res, { requests, count: records.length }); return true; }
  if (path === '/fixture/team/unavailable') { unavailable = url.searchParams.get('value') === 'true'; json(res, {}); return true; }
  if (path === '/fixture/team/reject-delete') { rejectDelete = url.searchParams.get('value') === 'true'; json(res, {}); return true; }
  if (path === '/fixture/team/release') { for (const release of held) release(); held = []; json(res, {}); return true; }
  if (path === '/api/v1/admin/directories/organizations/lookup') {
    const selected = url.searchParams.get('selectedId'), search = (url.searchParams.get('search') || '').toLowerCase();
    json(res, organizations.filter(item => selected ? item.id === selected : item.name.toLowerCase().includes(search))); return true;
  }
  if (path === '/api/v1/users/query') {
    requests.push({ method: req.method, path, query: Object.fromEntries(url.searchParams) });
    if (unavailable) { json(res, {}, 503); return true; }
    const search = (url.searchParams.get('search') || '').toLowerCase();
    const organization = url.searchParams.get('organizationId'), role = url.searchParams.get('role'), state = url.searchParams.get('state'), cohort = url.searchParams.get('isTestUser');
    let rows = records.filter(item => (!search || search === 'held' || `${item.name} ${item.email} ${item.organizationName}`.toLowerCase().includes(search)) &&
      (!organization || item.organizationId === organization) && (!role || item.role === role) && (!state || item.accountState === state) && (!cohort || String(item.isTestUser) === cohort));
    const sort = url.searchParams.get('sortBy') || 'name', descending = url.searchParams.get('descending') === 'true';
    const property = { name: 'name', email: 'email', organization: 'organizationName', state: 'accountState' }[sort] || 'name';
    rows = rows.slice().sort((a, b) => (a[property].localeCompare(b[property]) * (descending ? -1 : 1)) || a.id.localeCompare(b.id));
    const size = Number(url.searchParams.get('pageSize') || 25), total = rows.length;
    const page = Math.min(Number(url.searchParams.get('page') || 1), Math.max(1, Math.ceil(total / size)));
    const response = () => json(res, { page, pageSize: size, total, items: rows.slice((page - 1) * size, page * size) });
    if (search === 'held') held.push(response); else response();
    return true;
  }
  if (path === '/api/v1/users' && req.method === 'GET') { json(res, records); return true; }
  const deletion = path.match(/^\/api\/v1\/users\/(team-user-\d+)$/);
  if (deletion && req.method === 'DELETE') {
    requests.push({ method: req.method, path });
    if (rejectDelete) json(res, { error: 'synthetic_dependency', message: 'Synthetic deletion rejected. Review the dependency and retry.' }, 409);
    else { records = records.filter(item => item.id !== deletion[1]); res.writeHead(204); res.end(); }
    return true;
  }
  const signIn = path.match(/^\/api\/v1\/local-auth\/users\/(team-user-\d+)\/(enable|disable|status)$/);
  if (signIn) {
    requests.push({ method: req.method, path });
    const row = records.find(item => item.id === signIn[1]);
    if (!row) json(res, {}, 404);
    else if (signIn[2] === 'status') json(res, { isEnabled: row.accountState === 'Enabled' });
    else { row.accountState = signIn[2] === 'enable' ? 'Enabled' : 'Disabled'; res.writeHead(204); res.end(); }
    return true;
  }
  return false;
}
