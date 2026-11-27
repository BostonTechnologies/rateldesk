const observers = new Map();
let nextId = 0;
let mutationPauses = 0;

function fingerprintOf(profile) {
    const sorted = values => [...new Set(values ?? [])].sort();
    const grants = profile.scopedPermissionGrants ?? [];
    const queueGrants = profile.ticketQueuePermissionGrants ?? [];
    return JSON.stringify({
        authenticated: profile.isAuthenticated,
        admin: profile.isHelpdeskAdmin,
        email: profile.email,
        primaryOrganization: profile.primaryOrganizationId,
        customer: profile.customerId,
        scoped: !!profile.usesScopedPermissions,
        // FromClaims includes effective permissions in both sets, whereas the API
        // DTO separates role bundles and permissions. Compare their common union.
        access: sorted([...(profile.permissions ?? []), ...(profile.roleBundles ?? [])]),
        organizations: profile.usesScopedPermissions
            ? sorted(grants.map(grant => grant.organizationId))
            : sorted(profile.allowedOrganizationIds),
        grants: sorted(grants.map(grant => `${grant.permission}|${grant.organizationId}`)),
        restrictedWorkforce: !!profile.hasRestrictedWorkforceAccess,
        queueGrants: sorted(queueGrants.map(grant => JSON.stringify([
            grant.permission, grant.organizationId, grant.supportGroupId, grant.ownerOrganizationId
        ])))
    });
}

export function start(initialProfile) {
    const id = ++nextId;
    let fingerprint = initialProfile ? fingerprintOf(initialProfile) : undefined;
    let activeRequest;
    let controller;
    let stopped = false;
    async function check() {
        controller = new AbortController();
        const timeout = setTimeout(() => controller?.abort(), 10_000);
        try {
            const response = await fetch('/session/access', {
                credentials: 'same-origin', cache: 'no-store', signal: controller.signal
            });
            if (stopped || mutationPauses > 0) return;
            if (response.status === 401 || response.status === 403) {
                location.replace('/login');
                return;
            }
            if (!response.ok) return;
            const profile = await response.json();
            if (stopped || mutationPauses > 0) return;
            const next = fingerprintOf(profile);
            if (fingerprint !== undefined && fingerprint !== next) location.reload();
            fingerprint = next;
        } catch { /* A transient outage is retried on the next interval. */ }
        finally { clearTimeout(timeout); controller = undefined; }
    }
    function refresh() {
        if (stopped || mutationPauses > 0 || activeRequest) return activeRequest ?? Promise.resolve();
        activeRequest = check().finally(() => { activeRequest = undefined; });
        return activeRequest;
    }
    const timer = setInterval(() => void refresh(), 30_000);
    const focused = () => { if (document.visibilityState === 'visible') void refresh(); };
    document.addEventListener('visibilitychange', focused);
    observers.set(id, {
        pending: () => activeRequest ?? Promise.resolve(),
        refresh,
        stop: () => {
            stopped = true;
            clearInterval(timer);
            document.removeEventListener('visibilitychange', focused);
            controller?.abort();
        }
    });
    void refresh();
    return id;
}

// Wait for already-issued polls to settle before rotating a session cookie.
// Pausing alone or firing an abort without awaiting it permits an older response
// to race the replacement cookie or redirect the user during MFA enrollment.
export async function pauseForAccountMutation() {
    mutationPauses++;
    await Promise.all([...observers.values()].map(observer => observer.pending()));
}

export function resumeAfterAccountMutation() {
    mutationPauses = Math.max(0, mutationPauses - 1);
    if (mutationPauses === 0) {
        for (const observer of observers.values()) void observer.refresh();
    }
}

export function stop(id) {
    observers.get(id)?.stop();
    observers.delete(id);
}
