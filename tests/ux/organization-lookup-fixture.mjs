export function organizationLookupPage(url, authorized) {
  const id = url.searchParams.get('id');
  const exclude = url.searchParams.get('exclude');
  const term = (url.searchParams.get('q') ?? '').toLocaleLowerCase();
  const eligible = authorized.filter(row => row.id !== exclude);
  const matches = eligible.filter(row => id ? row.id === id :
    [row.name, row.dnsName].some(value => value?.toLocaleLowerCase().includes(term)))
    .sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
  const limit = Math.min(50, Number(url.searchParams.get('limit') || 20));
  return { items: matches.slice(0, limit), totalEligible: eligible.length, hasMore: matches.length > limit };
}
