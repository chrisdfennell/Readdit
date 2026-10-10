'use strict';

// The New post window's text editor: detail.js's markdownEditor (toolbar, Markdown or rich text),
// reporting its Markdown to the window as you type.
(() => {
    const mount = document.getElementById('editor-root');
    let made = null;

    host.addEventListener('message', e => {
        const m = e.data;
        if (m.type === 'editorInit') {
            document.documentElement.dataset.theme = m.theme;
            if (m.accent) document.documentElement.style.setProperty('--accent', m.accent);
            state.settings = m.settings || {}; // saved replies for the 💬 menu
            made = markdownEditor(m.text || '', m.placeholder || '');
            mount.replaceChildren(made.editor, made.preview);
            made.textarea.addEventListener('input', () => send({ type: 'editorText', text: made.textarea.value }));
            made.editor.focusInput();
        } else if (m.type === 'editorSet' && made) {
            made.editor.setText(m.text || '');
        } else if (m.type === 'editorFocus' && made) {
            made.editor.focusInput();
        }
    });
    send({ type: 'editorReady' });
})();
