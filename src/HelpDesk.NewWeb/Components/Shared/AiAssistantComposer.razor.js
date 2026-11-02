const bindings = new WeakMap();
export function resize(root) {
    const input = root.querySelector('textarea');
    input.style.height = 'auto';
    input.style.height = `${Math.min(240, Math.max(64, input.scrollHeight))}px`;
}
export function attach(root) {
    detach(root);
    const controller = new AbortController();
    bindings.set(root, controller);
    const input = root.querySelector('textarea');
    const options = { signal: controller.signal };
    let composing = false;
    input.addEventListener('compositionstart', () => composing = true, options);
    input.addEventListener('compositionend', () => composing = false, options);
    input.addEventListener('input', () => resize(root), options);
    input.addEventListener('keydown', event => {
        if (event.key !== 'Enter' || event.shiftKey || event.isComposing || composing || event.keyCode === 229) return;
        const modifier = event.ctrlKey || event.metaKey;
        if (root.dataset.submitModifier === 'true' && !modifier) return;
        if (!modifier && !matchMedia('(pointer: fine)').matches) return;
        event.preventDefault();
        const send = root.querySelector('[data-chat-send]');
        if (input.value.trim() && !input.disabled && !send.disabled) send.click();
    }, options);
    resize(root);
}
export function focus(root) { root.querySelector('textarea')?.focus(); }
export function detach(root) {
    bindings.get(root)?.abort();
    bindings.delete(root);
}
