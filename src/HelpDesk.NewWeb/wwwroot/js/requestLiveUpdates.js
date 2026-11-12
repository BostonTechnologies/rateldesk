export function observe(receiver) {
    let disposed = false;
    const resume = () => {
        if (!disposed && document.visibilityState === 'visible') {
            receiver.invokeMethodAsync('ResumeUpdatesAsync').catch(() => dispose());
        }
    };
    const dispose = () => {
        disposed = true;
        document.removeEventListener('visibilitychange', resume);
        window.removeEventListener('pageshow', resume);
        window.removeEventListener('online', resume);
    };
    document.addEventListener('visibilitychange', resume);
    window.addEventListener('pageshow', resume);
    window.addEventListener('online', resume);
    return { dispose };
}
