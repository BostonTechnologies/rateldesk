export { ensureBlazorMonaco } from './helpdesk-monaco-loader.js';

// BlazorMonaco disposes its .NET callback reference separately from the browser
// editor. These prompt editors create their own models, so release both and the
// holder when changing prompts or leaving the library.
export function disposeEditor(id) {
    const holders = window.blazorMonaco?.editors;
    if (!holders) return;
    const index = holders.findIndex(holder => holder.id === id);
    if (index < 0) return;
    const editor = holders[index].editor;
    const model = editor.getModel();
    editor.dispose();
    if (model && !model.isDisposed()) model.dispose();
    holders.splice(index, 1);
}
