const prefix = 'operator-chat-draft.v1.';
const actorKey = prefix + 'actor';
const draftLifetimeMs = 8 * 60 * 60 * 1000;

function selectActor(key) {
    const actor = JSON.parse(key)[0];
    const previous = sessionStorage.getItem(actorKey);
    if (previous !== actor) {
        for (const stored of Object.keys(sessionStorage)) {
            if (stored.startsWith(prefix)) sessionStorage.removeItem(stored);
        }
        sessionStorage.setItem(actorKey, actor);
    }
}

function load(key) {
    const draft = JSON.parse(sessionStorage.getItem(prefix + key));
    if (draft && !draft.pending && Date.now() - draft.changedAt > draftLifetimeMs) {
        sessionStorage.removeItem(prefix + key);
        return null;
    }
    return draft;
}

export function read(key) {
    if (!key) return null;
    try {
        selectActor(key);
        return load(key);
    } catch {
        return null;
    }
}

export function write(key, draft) {
    if (!key) return false;
    try {
        // An old circuit must never store drafts after the tab changed accounts.
        if (sessionStorage.getItem(actorKey) !== JSON.parse(key)[0]) return false;
        if (!draft?.text && !draft?.pending) {
            sessionStorage.removeItem(prefix + key);
        } else {
            for (const stored of Object.keys(sessionStorage)) {
                if (stored !== actorKey && stored.startsWith(prefix)) load(stored.slice(prefix.length));
            }
            sessionStorage.setItem(prefix + key, JSON.stringify({ ...draft, changedAt: Date.now() }));
        }
        return true;
    } catch {
        return false;
    }
}

export function saveText(key, text) {
    const draft = readCurrent(key);
    if (key) write(key, { ...draft, text });
}

function readCurrent(key) {
    if (!key) return null;
    try {
        return sessionStorage.getItem(actorKey) === JSON.parse(key)[0] ? load(key) : null;
    } catch {
        return null;
    }
}

export function clearActor(actor) {
    if (!actor || sessionStorage.getItem(actorKey) !== actor) return;
    for (const stored of Object.keys(sessionStorage)) {
        if (stored.startsWith(prefix)) sessionStorage.removeItem(stored);
    }
}
