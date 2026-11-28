// Only the parent uses its Web session. The opaque-origin child receives raster bytes.
export function observe(frame, receiver, assetUrls) {
    const cancellation = new AbortController();
    let assetsStarted = false;
    const loadAssets = async () => {
        for (const [index, url] of assetUrls.entries()) {
            if (cancellation.signal.aborted) return;
            try {
                const response = await fetch(url, {
                    credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: cancellation.signal
                });
                if (!response.ok || !['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(response.headers.get('Content-Type'))) continue;
                const blob = await response.blob();
                if (!cancellation.signal.aborted) frame.contentWindow?.postMessage({ type: 'rateldesk-email-asset', index, blob }, '*');
            } catch {
                // Leave the email image's alternative text visible on failure.
            }
        }
    };
    const receive = event => {
        if (event.source !== frame.contentWindow || !event.data) return;
        if (event.data.type === 'rateldesk-email-size' && Number.isFinite(event.data.height))
            frame.style.height = `${Math.min(4000, Math.max(160, event.data.height))}px`;
        if (event.data.type === 'rateldesk-email-escape') receiver.invokeMethodAsync('DismissPreview');
        if (event.data.type === 'rateldesk-email-assets-ready' && !assetsStarted) {
            assetsStarted = true;
            void loadAssets();
        }
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
    return { dispose: () => { cancellation.abort(); window.removeEventListener('message', receive); layout.disconnect(); } };
}
