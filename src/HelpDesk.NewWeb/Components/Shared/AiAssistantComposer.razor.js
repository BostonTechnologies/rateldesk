const bindings = new WeakMap();
export function resize(root) {
    const input = root.querySelector('textarea');
    input.style.height = 'auto';
    input.style.height = `${Math.min(240, Math.max(64, input.scrollHeight))}px`;
}
export function attach(root, dotnet) {
    detach(root);
    const controller = new AbortController();
    bindings.set(root, controller);
    const input = root.querySelector('textarea');
    const options = { signal: controller.signal };
    let composing = false;
    let sending = false;
    input.addEventListener('compositionstart', () => composing = true, options);
    input.addEventListener('compositionend', () => composing = false, options);
    input.addEventListener('input', () => resize(root), options);
    input.addEventListener('keydown', event => {
        if (event.key !== 'Enter' || event.isComposing || composing || event.keyCode === 229) return;
        if (event.repeat) { event.preventDefault(); return; }
        if (event.altKey && !event.ctrlKey && !event.metaKey) {
            event.preventDefault();
            if (input.disabled || input.readOnly) return;
            input.setRangeText('\n', input.selectionStart, input.selectionEnd, 'end');
            input.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertLineBreak', data: '\n' }));
            resize(root);
            return;
        }
        if (event.altKey || event.shiftKey) return;
        event.preventDefault();
        const send = root.querySelector('[data-chat-send]');
        if (!input.value.trim() || input.disabled || input.readOnly || sending || !send) return;
        sending = true;
        dotnet.invokeMethodAsync('SubmitFromInputAsync', input.value)
            .catch(error => console.warn('Chat submission was interrupted.', error?.name))
            .finally(() => sending = false);
    }, options);
    resize(root);
}
export function focus(root) { root.querySelector('textarea')?.focus(); }
export function detach(root) {
    bindings.get(root)?.abort();
    bindings.delete(root);
}
