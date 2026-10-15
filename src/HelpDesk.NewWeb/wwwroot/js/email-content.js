// The opaque-origin child can only report a bounded height or request dismissal.
// Ignore messages from any other frame; never read or execute email content.
export function observe(frame, receiver) {
    const receive = event => {
        if (event.source !== frame.contentWindow || !event.data) return;
        if (event.data.type === 'rateldesk-email-size' && Number.isFinite(event.data.height))
            frame.style.height = `${Math.min(4000, Math.max(160, event.data.height))}px`;
        if (event.data.type === 'rateldesk-email-escape') receiver.invokeMethodAsync('DismissPreview');
    };
    // CSS zoom changes the layout root size without changing media-query widths.
    // Keep enlarged preview controls/content inside that actual layout viewport.
    const fitZoomedPreview = () => {
        const popover = frame.closest('.incident-preview-popover');
        const body = popover?.querySelector('.incident-preview-body');
        if (!popover || !body) return;
        const scale = Math.max(1, Number.parseFloat(getComputedStyle(document.documentElement).zoom) || 1);
        const zoomed = scale > 1;
        popover.dataset.emailCssZoom = zoomed ? 'true' : 'false';
        if (zoomed) {
            popover.style.maxWidth = `${Math.max(100, window.innerWidth / scale - 16)}px`;
            body.style.maxWidth = '100%';
            body.style.maxHeight = `${Math.max(100, window.innerHeight / scale - 40)}px`;
        } else {
            popover.style.removeProperty('max-width');
            body.style.removeProperty('max-width');
            body.style.removeProperty('max-height');
        }
    };
    const layout = new ResizeObserver(fitZoomedPreview);
    layout.observe(document.documentElement);
    fitZoomedPreview();
    window.addEventListener('message', receive);
    frame.contentWindow?.postMessage('rateldesk-email-measure', '*');
    return { dispose: () => { window.removeEventListener('message', receive); layout.disconnect(); } };
}
