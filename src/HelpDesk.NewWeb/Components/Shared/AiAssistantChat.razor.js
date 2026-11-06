const active = new WeakMap();
const popovers = new WeakMap();
const key = actor => `helpdesk.chat.display.v1.${actor || 'operator'}`;
export function readPreferences(actor) { try { return localStorage.getItem(key(actor)); } catch { return null; } }
export function savePreferences(actor, value) { try { localStorage.setItem(key(actor), value); } catch { } }
export function watchPopover(root, dotnet, kind) {
    if (!root?.querySelector || popovers.has(root)) return;
    const controller = new AbortController();
    popovers.set(root, controller);
    const options = { capture: true, signal: controller.signal };
    const current = () => root.querySelector(`[data-chat-popover="${kind}"]`);
    const trigger = () => root.querySelector(`[data-chat-popover-trigger="${kind}"]`);
    let scheduled, observedPanel;
    const position = () => {
        const panel = current(), button = trigger();
        if (observedPanel !== panel) {
            if (observedPanel) resized.unobserve(observedPanel);
            observedPanel = panel;
            if (panel) resized.observe(panel);
        }
        if (!panel || !button || !button.getClientRects().length) return;
        if (!panel.matches(':popover-open')) panel.showPopover();
        const viewport = window.visualViewport;
        const left = viewport?.offsetLeft || 0, top = viewport?.offsetTop || 0;
        const width = viewport?.width || innerWidth, height = viewport?.height || innerHeight;
        const gap = 8;
        Object.assign(panel.style, { position: 'fixed', inset: 'auto', margin: '0',
            maxWidth: `${Math.max(0, width - 2 * gap)}px`, maxHeight: `${Math.max(0, height - 2 * gap)}px` });
        const anchor = button.getBoundingClientRect(), bounds = panel.getBoundingClientRect();
        const below = top + height - anchor.bottom - gap;
        const above = anchor.top - top - gap;
        const y = bounds.height <= below || below >= above ? anchor.bottom + gap : anchor.top - bounds.height - gap;
        panel.style.left = `${Math.max(left + gap, Math.min(anchor.right - bounds.width, left + width - bounds.width - gap))}px`;
        panel.style.top = `${Math.max(top + gap, Math.min(y, top + height - bounds.height - gap))}px`;
    };
    const schedule = () => { cancelAnimationFrame(scheduled); scheduled = requestAnimationFrame(position); };
    const observer = new MutationObserver(schedule);
    observer.observe(root, { childList: true, subtree: true });
    const resized = new ResizeObserver(schedule);
    resized.observe(root);
    window.addEventListener('resize', schedule, options);
    document.addEventListener('scroll', schedule, options);
    window.visualViewport?.addEventListener('resize', schedule, options);
    window.visualViewport?.addEventListener('scroll', schedule, options);
    controller.signal.addEventListener('abort', () => { observer.disconnect(); resized.disconnect(); cancelAnimationFrame(scheduled); });
    schedule();
    const outside = event => {
        const panel = current();
        if (panel && !panel.contains(event.target) && !trigger()?.contains(event.target)) dotnet.invokeMethodAsync('ClosePopoverAsync');
    };
    document.addEventListener('pointerdown', outside, options);
    document.addEventListener('focusin', outside, options);
    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape' || !current() || nestedOpen()) return;
        event.preventDefault(); event.stopImmediatePropagation();
        dotnet.invokeMethodAsync('ClosePopoverAsync'); trigger()?.focus({ preventScroll: true });
    }, options);
}
export function unwatchPopover(root) { popovers.get(root)?.abort(); popovers.delete(root); }
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
    requestAnimationFrame(() => {
        // Preserve a composer or dialog the operator focused before this frame ran.
        if (active.get(root) !== saved || root.contains(document.activeElement) || nestedOpen()) return;
        root.querySelector('[aria-label="Restore conversation"]')?.focus({ preventScroll: true });
    });
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
