const active = new WeakMap();
const key = actor => `helpdesk.chat.display.v1.${actor || 'operator'}`;
export function readPreferences(actor) { try { return localStorage.getItem(key(actor)); } catch { return null; } }
export function savePreferences(actor, value) { try { localStorage.setItem(key(actor), value); } catch { } }
const nestedOpen = () => [...document.querySelectorAll('.mud-dialog-container, .mud-popover-open')].some(x =>
    x.getClientRects().length && getComputedStyle(x).visibility !== 'hidden'
    && !x.matches('.mud-tooltip') && !x.querySelector('.mud-tooltip')
    && (x.matches('.mud-dialog-container') || x.querySelector('[role="listbox"], .mud-menu-list, .mud-list')));
export function maximise(root, dotnet) {
    if (active.has(root)) return;
    const saved = { focus: document.activeElement, overflow: document.body.style.overflow, inert: [] };
    // Keep ancestor path and overlay providers available, including nested approval dialogs.
    for (let node = root; node?.parentElement && node !== document.body; node = node.parentElement) {
        for (const sibling of node.parentElement.children) {
            if (sibling === node || sibling.matches('script, style, .mud-dialog-provider, .mud-popover-provider, .mud-snackbar-provider') || sibling.querySelector('.mud-dialog-provider, .mud-popover-provider')) continue;
            saved.inert.push([sibling, sibling.inert]); sibling.inert = true;
        }
    }
    document.body.style.overflow = 'hidden';
    document.body.classList.add('chat-conversation-maximised');
    saved.handler = event => {
        if (nestedOpen()) return;
        if (event.key === 'Escape') {
            event.preventDefault(); event.stopPropagation(); dotnet.invokeMethodAsync('RestoreConversationAsync');
        } else if (event.key === 'Tab') {
            const controls = [...root.querySelectorAll('button, a[href], input, textarea, select, summary, [tabindex="0"]')].filter(x => !x.disabled && x.getClientRects().length);
            if (!controls.length) return;
            const first = controls[0], last = controls.at(-1);
            if (event.shiftKey && (document.activeElement === first || !root.contains(document.activeElement))) { event.preventDefault(); last.focus(); }
            if (!event.shiftKey && (document.activeElement === last || !root.contains(document.activeElement))) { event.preventDefault(); first.focus(); }
        }
    };
    document.addEventListener('keydown', saved.handler);
    active.set(root, saved);
    requestAnimationFrame(() => root.querySelector('[aria-label="Restore conversation"]')?.focus());
}
export function restore(root) {
    const saved = active.get(root); if (!saved) return;
    document.removeEventListener('keydown', saved.handler);
    for (const [node, inert] of saved.inert) node.inert = inert;
    document.body.style.overflow = saved.overflow;
    document.body.classList.remove('chat-conversation-maximised');
    if (saved.focus?.isConnected) saved.focus.focus({ preventScroll: true });
    active.delete(root);
}
