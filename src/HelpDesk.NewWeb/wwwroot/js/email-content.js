// The opaque-origin child can only report a bounded height or request dismissal.
// Ignore messages from any other frame; never read or execute email content.
export function observe(frame, receiver) {
    const receive = event => {
        if (event.source !== frame.contentWindow || !event.data) return;
        if (event.data.type === 'rateldesk-email-size' && Number.isFinite(event.data.height))
            frame.style.height = `${Math.min(4000, Math.max(160, event.data.height))}px`;
        if (event.data.type === 'rateldesk-email-escape') receiver.invokeMethodAsync('DismissPreview');
    };
    window.addEventListener('message', receive);
    frame.contentWindow?.postMessage('rateldesk-email-measure', '*');
    return { dispose: () => window.removeEventListener('message', receive) };
}
