const reviews = new Map();
const surfaces = new WeakMap();
let nextId = 0;
export function openReview(surface, explicitOpener, openerId) {
    if (!surface) return 0;
    if (surfaces.has(surface)) return surfaces.get(surface);
    const id = ++nextId;
    const opener = (openerId && document.getElementById(openerId)) || (explicitOpener?.isConnected ? explicitOpener : document.activeElement);
    const focusable = () => [...surface.querySelectorAll('button, input, select, textarea, a[href], [tabindex]')]
        .filter(item => !item.disabled && item.tabIndex >= 0 && item.getClientRects().length);
    const trap = event => {
        if (event.key === 'Escape' && surface.querySelector('form[data-submitting="true"]')) { event.preventDefault(); event.stopPropagation(); return; }
        if (event.key !== 'Tab') return;
        const items = focusable();
        const first = items[0], last = items[items.length - 1];
        if (!first) { event.preventDefault(); surface.focus(); return; }
        if (event.shiftKey && (document.activeElement === first || document.activeElement === surface)) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && (document.activeElement === last || document.activeElement === surface)) { event.preventDefault(); first.focus(); }
    };
    surface.addEventListener('keydown', trap);
    const submissions = [];
    for (const form of surface.querySelectorAll('form[data-preserve-consent]')) {
        let submitting = false;
        const submit = async event => {
            event.preventDefault();
            if (submitting) return;
            submitting = true;
            form.dataset.submitting = 'true';
            const fields = new FormData(form);
            if (event.submitter?.name) fields.set(event.submitter.name, event.submitter.value);
            const controls = [...form.querySelectorAll('button')];
            const previous = controls.map(control => control.disabled);
            controls.forEach(control => control.disabled = true);
            const feedback = form.querySelector('.consent-feedback');
            try {
                const response = await fetch(form.action, { method: 'POST', body: fields, credentials: 'same-origin', headers: { Accept: 'application/json' } });
                if (!response.ok) throw new Error('unavailable');
                const result = await response.json();
                if (result.status === 'approved' || result.status === 'denied') { window.location.assign(result.redirectUrl); return; }
                if (feedback) {
                    feedback.textContent = result.status === 'slow-down' ? 'Too many attempts. Wait a few minutes and retry.' :
                        result.status === 'form-expired' ? 'This form expired. Refresh the page, then review the code again.' :
                        'Approval could not complete. Your selection is retained. Retry, or close and review current access.';
                    feedback.hidden = false;
                }
            } catch {
                if (feedback) { feedback.textContent = 'The connection service is unavailable. Your selection is retained; retry when it is reachable.'; feedback.hidden = false; }
            }
            controls.forEach((control, index) => control.disabled = previous[index]);
            submitting = false;
            form.dataset.submitting = 'false';
            event.submitter?.focus();
        };
        form.addEventListener('submit', submit);
        submissions.push({ form, submit });
    }
    reviews.set(id, { surface, opener, openerId, trap, submissions });
    surfaces.set(surface, id);
    surface.focus();
    return id;
}
export function closeReview(id) {
    const review = reviews.get(id);
    if (!review) return;
    review.surface.removeEventListener('keydown', review.trap);
    for (const { form, submit } of review.submissions) form.removeEventListener('submit', submit);
    reviews.delete(id);
    surfaces.delete(review.surface);
    const opener = (review.openerId && document.getElementById(review.openerId)) || review.opener;
    if (opener?.isConnected) opener.focus();
}
