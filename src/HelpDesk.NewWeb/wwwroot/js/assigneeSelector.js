export function focusSearch(input, trigger) {
    if (!input) return;
    if (!input.dataset.assigneeKeys) {
        input.dataset.assigneeKeys = 'true';
        input.addEventListener('keydown', event => {
            // Tab may visit the explicit Retry/Unassigned action. When leaving
            // the portal, continue the surrounding form's order at the trigger.
            const actions = input.closest('[role="dialog"]')?.querySelector('.assignee-clear:not(:disabled), .assignee-retry');
            if (event.key === 'Tab' && !event.shiftKey && actions) {
                event.preventDefault();
                actions.focus({ preventScroll: true });
            }
            if (event.key === 'Tab' && (event.shiftKey || !actions)) trigger?.focus({ preventScroll: true });
            // Keep arrow navigation in the list and Enter out of the enclosing form.
            if (['ArrowDown', 'ArrowUp', 'Enter', 'Escape'].includes(event.key)) event.preventDefault();
        });
        input.closest('[role="dialog"]')?.addEventListener('keydown', event => {
            if (event.target !== input && event.key === 'Tab' && !event.shiftKey) trigger?.focus({ preventScroll: true });
        });
    }
    input.focus({ preventScroll: true });
}

export function scrollOption(id) {
    document.getElementById(id)?.scrollIntoView({ block: 'nearest' });
}
