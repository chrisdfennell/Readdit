'use strict';

// Renders a Reddit post and its comments from Reddit's own JSON. Anything that needs the
// user's session (votes, saves, replies, more comments) is asked of the host app.

const host = window.chrome && window.chrome.webview;
const root = document.getElementById('root');
const empty = document.getElementById('empty');
const toastEl = document.getElementById('toast');

const COMMENT_SORTS = [
    ['confidence', 'Best'], ['top', 'Top'], ['new', 'New'],
    ['controversial', 'Controversial'], ['old', 'Old'], ['qa', 'Q&A'],
];

const state = { post: null, settings: {}, sort: 'confidence', hls: [], audios: [], lastVisit: null, localMedia: null };

/** Swap a remote media URL for its offline-library copy when one exists. */
const mapUrl = url => (url && state.localMedia && state.localMedia[url]) || url;

const sameUser = (a, b) => (a || '').toLowerCase() === (b || '').toLowerCase();
const isMuted = name => (state.settings.muted || []).some(u => sameUser(u, name));
const isHighlighted = name => (state.settings.highlighted || []).some(u => sameUser(u, name));

// ---- Host messaging --------------------------------------------------------------------

let reqSeq = 0;
const pending = new Map();

function send(message) {
    if (host) host.postMessage(message);
}

function call(type, args = {}) {
    return new Promise((resolve, reject) => {
        const reqId = ++reqSeq;
        pending.set(reqId, { resolve, reject });
        send({ type, reqId, ...args });
    });
}

if (host) {
    host.addEventListener('message', e => {
        const m = e.data;
        switch (m.type) {
            case 'reply': {
                const p = pending.get(m.reqId);
                if (!p) return;
                pending.delete(m.reqId);
                if (m.ok) p.resolve(m.data);
                else p.reject(new Error(m.error || 'Request failed'));
                break;
            }
            case 'clear': clearView(); break;
            case 'loading': clearView(); showLoading(); break;
            case 'post':
                state.localMedia = m.localMedia || null;
                renderPost(m.data, m.settings);
                break;
            case 'download': downloadPost(); break;
            case 'thread': renderThread(m); break;
            case 'message': renderMessage(m.data, m.settings); break;
            case 'conversation': renderConversation(m); break;
            case 'error': showError(m.message); break;
            case 'scroll':
                window.scrollBy({ top: m.direction * window.innerHeight * 0.85, behavior: 'smooth' });
                break;
            case 'postState': syncPostState(m); break;
            case 'display': applyDisplay(m); break;
            case 'theme':
                document.documentElement.dataset.theme = m.theme;
                document.documentElement.style.setProperty('--accent', m.accent);
                break;
            case 'navComments': navigateComments(m.direction); break;
            case 'find': openFind(); break;
            case 'nextNew': nextNewComment(); break;
            case 'nextOp': nextOpComment(); break;
            case 'fullscreenState':
                viewer.fullscreen = !!m.on;
                if (!m.on) viewer.ownsFullscreen = false;
                fullButton.classList.toggle('on', viewer.fullscreen);
                break;
            case 'viewer': {
                const pics = state.post ? postImages(state.post) : [];
                if (pics.length) {
                    openLightbox(pics, 0, true);
                } else {
                    // No pictures here: start from the next post that has some.
                    showToast('Finding the next post with pictures…');
                    stepPost(1);
                }
                break;
            }
            case 'settings':
                state.settings = m.settings;
                refreshUserTags();
                break;
        }
    });
}

// ---- Small helpers ---------------------------------------------------------------------

function el(tag, attrs, ...children) {
    const node = document.createElement(tag);
    if (attrs) {
        for (const [key, value] of Object.entries(attrs)) {
            if (value == null || value === false) continue;
            if (key === 'class') node.className = value;
            else if (key.startsWith('on')) node.addEventListener(key.slice(2), value);
            else node.setAttribute(key, value === true ? '' : value);
        }
    }
    for (const child of children.flat(Infinity)) {
        if (child == null || child === false) continue;
        node.append(child instanceof Node ? child : String(child));
    }
    return node;
}

function count(n) {
    const a = Math.abs(n);
    if (a >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, '') + 'm';
    if (a >= 1e4) return Math.round(n / 1e3) + 'k';
    if (a >= 1e3) return (n / 1e3).toFixed(1).replace(/\.0$/, '') + 'k';
    return String(n);
}

function age(seconds) {
    const mins = (Date.now() / 1000 - seconds) / 60;
    if (mins < 1) return 'just now';
    if (mins < 60) return Math.floor(mins) + 'm';
    if (mins < 1440) return Math.floor(mins / 60) + 'h';
    if (mins < 43200) return Math.floor(mins / 1440) + 'd';
    if (mins < 525600) return Math.floor(mins / 43200) + 'mo';
    return Math.floor(mins / 525600) + 'y';
}

function fullDate(seconds) {
    return new Date(seconds * 1000).toLocaleString();
}

const weight = likes => (likes === true ? 1 : likes === false ? -1 : 0);

function link(text, href, cls = 'plain') {
    return el('a', { class: cls, href }, text);
}

function action(label, onclick, title) {
    return el('button', { class: 'action', onclick, title }, label);
}

function spinner() {
    return el('div', { class: 'spinner' });
}

let toastTimer;
function showToast(text, actionLabel, onAction, duration) {
    toastEl.replaceChildren(text);
    if (actionLabel) {
        toastEl.append(el('button', {
            class: 'toast-action',
            onclick: () => {
                toastEl.hidden = true;
                onAction();
            },
        }, actionLabel));
    }
    toastEl.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { toastEl.hidden = true; }, duration || (actionLabel ? 7000 : 2600));
}

// ---- Markdown preview ------------------------------------------------------------------

/** Renders Reddit-flavoured Markdown for previews (Reddit renders the real thing on save). */
function renderMarkdown(text) {
    const withSpoilers = text
        .replace(/>!([\s\S]+?)!</g, '<span class="md-spoiler-text">$1</span>')
        .replace(/\^\(([^)]+)\)/g, '<sup>$1</sup>')
        .replace(/\^(\S+)/g, '<sup>$1</sup>');
    const html = window.marked
        ? window.marked.parse(withSpoilers, { gfm: true, breaks: false })
        : '<p>' + text.replace(/[&<>]/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[ch])) + '</p>';
    return sanitize(html);
}

/** A textarea with a live preview underneath; used for replies and edits. */
function markdownEditor(initial, placeholder) {
    const textarea = el('textarea', { placeholder, spellcheck: 'true' });
    textarea.value = initial || '';
    const preview = el('div', { class: 'md preview', hidden: true });
    let timer;
    const update = () => {
        const text = textarea.value.trim();
        preview.hidden = !text;
        if (text) preview.replaceChildren(el('div', { class: 'preview-label' }, 'Preview'), renderMarkdown(textarea.value));
    };
    textarea.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(update, 120);
    });
    update();
    return { textarea, preview };
}

// ---- Sanitizing Reddit's rendered HTML -------------------------------------------------

const BLOCKED_TAGS = new Set([
    'SCRIPT', 'STYLE', 'IFRAME', 'OBJECT', 'EMBED', 'FORM', 'INPUT', 'BUTTON',
    'TEXTAREA', 'SELECT', 'LINK', 'META', 'BASE', 'SVG', 'MATH',
]);

/** Attributes that hold a URL a browser might load or navigate to. */
const URL_ATTRIBUTES = new Set(['href', 'src', 'action', 'formaction', 'xlink:href', 'data', 'poster', 'background', 'cite']);

/**
 * True for javascript:, vbscript: and data: URLs. Browsers ignore tabs, newlines and other
 * control characters inside a URL (so "java\tscript:" still runs), so strip them before checking.
 */
function isScriptUrl(value) {
    const normalized = value.replace(/[\u0000- \u007f-\u009f]/g, '').toLowerCase();
    return /^(javascript|vbscript|data):/.test(normalized);
}

function sanitize(html, mediaMetadata) {
    const tpl = document.createElement('template');
    tpl.innerHTML = html || '';
    const walk = node => {
        for (const child of [...node.children]) {
            if (BLOCKED_TAGS.has(child.tagName.toUpperCase())) {
                child.remove();
                continue;
            }
            for (const attr of [...child.attributes]) {
                const name = attr.name.toLowerCase();
                if (name.startsWith('on') || name === 'style' || name === 'srcdoc' || name === 'srcset')
                    child.removeAttribute(attr.name);
                else if (URL_ATTRIBUTES.has(name) && isScriptUrl(attr.value))
                    child.removeAttribute(attr.name);
            }
            walk(child);
        }
    };
    walk(tpl.content);

    linkMediaMarkdown(tpl.content);
    inlineMedia(tpl.content, mediaMetadata);
    return tpl.content;
}

/**
 * Reddit leaves media markdown like ![gif](giphy|abc123) or ![img](id) as plain text in
 * body_html. Turn those into links the media step understands, but only in ordinary text
 * (not code, where someone may be explaining the syntax, and not inside existing links).
 */
function linkMediaMarkdown(root) {
    const pattern = /!\[(?:gif|img)\]\(([^)\s]+)\)/gi;
    const hits = [];
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
        if (n.parentElement && n.parentElement.closest('code, pre, a')) continue;
        pattern.lastIndex = 0;
        if (pattern.test(n.nodeValue)) hits.push(n);
    }
    for (const node of hits) {
        const text = node.nodeValue;
        const parts = document.createDocumentFragment();
        let last = 0;
        pattern.lastIndex = 0; // test() above moved it past the match, and matchAll starts from there
        for (const match of text.matchAll(pattern)) {
            parts.append(text.slice(last, match.index));
            const link = document.createElement('a');
            link.setAttribute('href', 'reddit-media:' + encodeURIComponent(match[1]));
            link.textContent = match[1];
            parts.append(link);
            last = match.index + match[0].length;
        }
        parts.append(text.slice(last));
        node.replaceWith(parts);
    }
}

/** Inline GIF videos only play while on screen, so a thread full of them stays light. */
const gifObserver = 'IntersectionObserver' in window
    ? new IntersectionObserver(entries => {
        for (const entry of entries) {
            if (entry.isIntersecting) entry.target.play().catch(() => {});
            else entry.target.pause();
        }
    }, { rootMargin: '300px 0px' })
    : null;

// ---- Inline GIFs and images in comments, post text and messages --------------------------

const MAX_INLINE_MEDIA = 8;

/** Media Reddit itself describes (uploads, Giphy picks): prefer the MP4 for animations. */
function fromMetadata(m) {
    if (!m || m.status !== 'valid' || !m.s) return null;
    if (m.e === 'AnimatedImage') {
        if (m.s.mp4) return { video: m.s.mp4 };
        if (m.s.gif) return { image: m.s.gif };
    }
    if (m.e === 'Image' && (m.s.u || m.s.gif)) return { image: m.s.u || m.s.gif };
    return null;
}

/** What a link points at, if it's a GIF, short looping video or Reddit-hosted image. */
function linkedMedia(href, meta) {
    meta = meta || {};
    if (href.startsWith('reddit-media:')) {
        const key = decodeURIComponent(href.slice('reddit-media:'.length));
        return fromMetadata(meta[key]) ||
            (key.startsWith('giphy|') ? { image: `https://media.giphy.com/media/${key.split('|')[1]}/giphy.gif` } : null);
    }

    let url;
    try {
        url = new URL(href, 'https://www.reddit.com');
    } catch {
        return null;
    }
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return null;
    const host = url.hostname.toLowerCase();
    const path = url.pathname.toLowerCase();

    // Giphy page or media links → the GIF itself (or Reddit's copy of it).
    const giphy = /giphy\.com$/.test(host) && /\/(?:gifs|media|embed)\/(?:[\w-]*-)?([A-Za-z0-9]{6,})/.exec(url.pathname);
    if (giphy) {
        const key = Object.keys(meta).find(k => k.startsWith('giphy|' + giphy[1]));
        return fromMetadata(key && meta[key]) || { image: `https://media.giphy.com/media/${giphy[1]}/giphy.gif` };
    }

    // Reddit uploads: the file name is the media_metadata key.
    if (host === 'preview.redd.it' || host === 'i.redd.it') {
        const id = path.split('/').pop().replace(/\.\w+$/, '');
        const known = fromMetadata(meta[id]);
        if (known) return known;
        if (url.searchParams.get('format') === 'mp4') return { video: url.href };
        if (/\.(gif|jpe?g|png|webp)$/.test(path)) return { image: url.href };
        return null;
    }

    if (/\.gifv$/.test(path)) return { video: url.href.replace(/\.gifv(\?|$)/i, '.mp4$1') };
    if (/\.(mp4|webm)$/.test(path) && /(^|\.)(imgur\.com|giphy\.com|tenor\.com|redd\.it|gfycat\.com)$/.test(host)) return { video: url.href };
    if (/\.gif$/.test(path)) return { image: url.href };
    return null;
}

function inlineMedia(fragment, meta) {
    let added = 0;
    for (const a of fragment.querySelectorAll('a[href]')) {
        if (added >= MAX_INLINE_MEDIA) break;
        const media = linkedMedia(a.getAttribute('href'), meta);
        if (!media) {
            // Media Reddit didn't describe: show a label rather than a dead "reddit-media:" link.
            if (a.getAttribute('href').startsWith('reddit-media:'))
                a.replaceWith(el('span', { class: 'muted' }, /giphy/i.test(a.textContent) ? '[GIF]' : '[image]'));
            continue;
        }

        // Videos start when scrolled into view (see gifObserver), not all at once.
        const node = media.video
            ? el('video', { src: mapUrl(media.video), class: 'md-gif', loop: true, muted: true, playsinline: true, preload: 'none',
                autoplay: !gifObserver })
            : el('img', { src: mapUrl(media.image), class: 'md-img', loading: 'lazy', alt: '' });
        if (media.video) node.muted = true;

        // A bare link (text is just the URL or a placeholder) becomes the media itself;
        // a link with its own wording keeps the text and shows the media right after it.
        const text = a.textContent.trim();
        const bare = text === '' || text === a.getAttribute('href') || /^(<image>|<gif>|image|gif|img)$/i.test(text) ||
            a.getAttribute('href').startsWith('reddit-media:') || (a.children.length === 1 && a.firstElementChild.tagName === 'IMG');
        if (bare) a.replaceWith(node);
        else a.after(node);
        added++;
    }
}

function mdNode(html, cls, mediaMetadata) {
    const node = el('div', { class: 'md ' + (cls || '') });
    node.append(sanitize(html, mediaMetadata));
    if (gifObserver) for (const video of node.querySelectorAll('video.md-gif')) gifObserver.observe(video);
    return node;
}

// ---- Votes and saves -------------------------------------------------------------------

function voteWidget(thing, large) {
    const widget = el('div', { class: 'vote' + (large ? ' large' : ''), 'data-id': thing.name });
    const up = el('button', { class: 'up', title: 'Upvote' }, '▲');
    const score = el('span', { class: 'score' });
    const down = el('button', { class: 'down', title: 'Downvote' }, '▼');
    widget.append(up, score, down);

    let likes = thing.likes ?? null;
    let value = thing.score ?? 0;
    const paint = () => {
        widget.classList.toggle('liked', likes === true);
        widget.classList.toggle('disliked', likes === false);
        score.textContent = thing.score_hidden ? '•' : count(value);
    };

    let voteSeq = 0;
    const vote = async target => {
        const previous = likes;
        const previousValue = value;
        const mine = ++voteSeq;
        value += weight(target) - weight(likes);
        likes = target;
        paint();
        try {
            await call('vote', { id: thing.name, dir: weight(target) });
        } catch (err) {
            // Only roll back if no newer vote has been cast since; that one owns the state now.
            if (mine === voteSeq) {
                likes = previous;
                value = previousValue;
                paint();
            }
            showToast('Vote failed: ' + err.message);
        }
    };

    up.onclick = () => vote(likes === true ? null : true);
    down.onclick = () => vote(likes === false ? null : false);
    if (thing.archived) {
        up.disabled = down.disabled = true;
        widget.title = 'Archived: voting is closed';
    }
    widget.sync = (newLikes, newScore) => {
        likes = newLikes;
        if (newScore != null) value = newScore;
        paint();
    };
    paint();
    return widget;
}

function saveButton(thing) {
    let saved = !!thing.saved;
    const button = el('button', { class: 'action', 'data-save-id': thing.name });
    const paint = () => {
        button.textContent = saved ? '★ Saved' : '☆ Save';
        button.classList.toggle('on', saved);
    };
    button.onclick = async () => {
        const target = !saved;
        saved = target;
        paint();
        try {
            await call('save', { id: thing.name, saved: target });
            showToast(target ? 'Saved' : 'Removed from saved');
        } catch (err) {
            saved = !target;
            paint();
            showToast('Save failed: ' + err.message);
        }
    };
    button.sync = value => {
        saved = value;
        paint();
    };
    paint();
    return button;
}

function syncPostState(m) {
    const widget = root.querySelector(`.vote[data-id="${m.id}"]`);
    if (widget && widget.sync) widget.sync(m.likes ?? null, m.score);
    const save = root.querySelector(`[data-save-id="${m.id}"]`);
    if (save && save.sync) save.sync(!!m.saved);
}

// ---- Drafts ----------------------------------------------------------------------------
// What you're writing in a reply or an edit is kept (in this browser profile) until you send or
// cancel it, so switching posts, closing Skein or a crash doesn't lose it.

const DRAFT_PREFIX = 'skein-draft:';
const DRAFT_DAYS = 30;

function loadDraft(key) {
    try {
        const draft = JSON.parse(localStorage.getItem(DRAFT_PREFIX + key));
        return draft && Date.now() - draft.at < DRAFT_DAYS * 864e5 ? draft.text : null;
    } catch {
        return null;
    }
}

function saveDraft(key, text, original = '') {
    try {
        if (text.trim() && text !== original) localStorage.setItem(DRAFT_PREFIX + key, JSON.stringify({ text, at: Date.now() }));
        else localStorage.removeItem(DRAFT_PREFIX + key);
    } catch {
        // Storage full or unavailable: drafts are a nicety.
    }
}

const clearDraft = key => saveDraft(key, '');

/** Forget drafts older than a month (once per page load). */
try {
    for (const key of Object.keys(localStorage)) {
        if (key.startsWith(DRAFT_PREFIX) && loadDraft(key.slice(DRAFT_PREFIX.length)) == null) localStorage.removeItem(key);
    }
} catch {
    // As above.
}

/** Saves the textarea as a draft while typing (shortly after each pause). */
function keepDraft(textarea, key, original = '') {
    let timer;
    textarea.addEventListener('input', () => {
        clearTimeout(timer);
        timer = setTimeout(() => saveDraft(key, textarea.value, original), 400);
    });
}

// ---- Replies ---------------------------------------------------------------------------

function replyBox(parentName, onPosted) {
    const draftKey = 'reply:' + parentName;
    const { textarea, preview } = markdownEditor('', 'Write a reply. Markdown is supported. Ctrl+Enter sends.');
    const status = el('span', { class: 'reply-status' });
    const submit = el('button', { class: 'primary' }, 'Reply');
    const cancel = el('button', null, 'Cancel');
    const box = el('div', { class: 'reply-box', hidden: true },
        textarea, el('div', { class: 'reply-actions' }, submit, cancel, status), preview);
    keepDraft(textarea, draftKey);

    // An unsent reply from before: put it back, with the box open so it's noticed.
    const draft = loadDraft(draftKey);
    if (draft) {
        textarea.value = draft;
        textarea.dispatchEvent(new Event('input'));
        box.hidden = false;
        status.textContent = 'Your unsent reply, as you left it';
    }

    cancel.onclick = () => {
        box.hidden = true;
        clearDraft(draftKey);
        textarea.value = '';
        textarea.dispatchEvent(new Event('input'));
        status.textContent = '';
    };
    submit.onclick = async () => {
        const text = textarea.value.trim();
        if (!text) return;
        submit.disabled = true;
        status.textContent = 'Posting…';
        try {
            const comment = await call('comment', { parent: parentName, text });
            clearDraft(draftKey);
            textarea.value = '';
            textarea.dispatchEvent(new Event('input'));
            clearDraft(draftKey); // the input event above schedules a save of the empty text
            status.textContent = '';
            box.hidden = true;
            onPosted(comment);
        } catch (err) {
            status.textContent = err.message;
        } finally {
            submit.disabled = false;
        }
    };
    textarea.addEventListener('keydown', e => {
        if (e.key === 'Enter' && e.ctrlKey) submit.click();
    });
    box.open = () => {
        box.hidden = false;
        textarea.focus();
    };
    return box;
}

// ---- Editing and deleting your own posts and comments -----------------------------------

/**
 * Swaps `target` for an editor until saved or cancelled. `save(text)` returns the updated
 * thing's data; `render(data)` builds the new body.
 */
function openEditor(target, initial, save, render, thingName) {
    const draftKey = 'edit:' + thingName;
    const draft = loadDraft(draftKey);
    const { textarea, preview } = markdownEditor(draft ?? initial, 'Edit. Markdown is supported. Ctrl+Enter saves.');
    const status = el('span', { class: 'reply-status' }, draft ? 'Your unsaved edit, as you left it' : '');
    const saveButton = el('button', { class: 'primary' }, 'Save edit');
    const cancel = el('button', null, 'Cancel');
    const editor = el('div', { class: 'reply-box' }, textarea, el('div', { class: 'reply-actions' }, saveButton, cancel, status), preview);
    keepDraft(textarea, draftKey, initial);
    target.replaceWith(editor);
    textarea.focus();

    cancel.onclick = () => {
        clearDraft(draftKey);
        editor.replaceWith(target);
    };
    saveButton.onclick = async () => {
        saveButton.disabled = true;
        status.textContent = 'Saving…';
        try {
            const data = await save(textarea.value);
            clearDraft(draftKey);
            editor.replaceWith(render(data));
            showToast('Edit saved');
        } catch (err) {
            status.textContent = err.message;
            saveButton.disabled = false;
        }
    };
    textarea.addEventListener('keydown', e => {
        if (e.key === 'Enter' && e.ctrlKey) saveButton.click();
    });
}

async function deleteThing(name, what) {
    if (!confirm(`Delete this ${what}? This can't be undone.`)) return false;
    try {
        await call('delete', { id: name });
        showToast(`Your ${what} was deleted`);
        return true;
    } catch (err) {
        showToast('Delete failed: ' + err.message);
        return false;
    }
}

// ---- Downloads -----------------------------------------------------------------------------

function hasDownloadableMedia(d) {
    const s = (d.crosspost_parent_list && d.crosspost_parent_list[0]) || d;
    const url = s.url_overridden_by_dest || s.url || '';
    return !!(s.is_gallery || s.is_video || s.post_hint === 'image' ||
        (s.secure_media && s.secure_media.reddit_video) || (s.preview && s.preview.reddit_video_preview) ||
        /\.(jpe?g|png|gif|webp|gifv|mp4|webm)(\?|$)/i.test(url));
}

async function downloadPost() {
    if (!state.post) {
        showToast('Wait for the post to finish loading, then try again');
        return;
    }
    if (!hasDownloadableMedia(state.post)) {
        showToast('This post has no images or video to download');
        return;
    }
    showToast('Downloading…', null, null, 60000);
    try {
        const result = await call('download', { post: state.post });
        if (result.files.length) {
            const text = `Saved ${result.files.length} ${result.files.length === 1 ? 'file' : 'files'}` +
                (result.note ? '. ' + result.note : '');
            showToast(text, 'Show in folder', () => send({ type: 'reveal', path: result.files[0] }), result.note ? 12000 : 7000);
        } else {
            showToast(result.note || 'Nothing was downloaded');
        }
    } catch (err) {
        showToast('Download failed: ' + err.message);
    }
}

// ---- Media -----------------------------------------------------------------------------

function previewUrl(s) {
    const image = s.preview && s.preview.images && s.preview.images[0];
    if (!image) return null;
    let best = null;
    for (const r of image.resolutions || []) if (r.width <= 1080) best = r.url;
    return mapUrl(best || (image.source && image.source.url) || null);
}

function youtubeId(url) {
    const m = /(?:youtube\.com\/(?:watch\?(?:.*&)?v=|shorts\/|embed\/)|youtu\.be\/)([\w-]{11})/i.exec(url);
    return m ? m[1] : null;
}

function videoPlayer(hlsUrl, mp4Url, poster, isGif) {
    const video = el('video', { controls: true, playsinline: true, preload: 'metadata', poster, loop: isGif });
    if (isGif) {
        video.muted = true;
        video.autoplay = true;
    }
    if (hlsUrl && window.Hls && window.Hls.isSupported()) {
        const hls = new window.Hls({ capLevelToPlayerSize: true });
        hls.loadSource(hlsUrl);
        hls.attachMedia(video);
        state.hls.push(hls);
    } else {
        video.src = mp4Url || hlsUrl;
    }
    return video;
}

/**
 * Reddit-hosted video. Reddit's HLS streams stall in hls.js (it starves its buffer and crawls at
 * a frame or two per second), so play the progressive MP4 instead. That file has no sound, so a
 * hidden audio element plays Reddit's separate audio file, kept in sync with the video.
 */
function redditVideo(fallbackUrl, poster, isGif) {
    const video = el('video', { controls: true, playsinline: true, preload: 'metadata', poster, loop: isGif, src: fallbackUrl });
    if (isGif) {
        video.muted = true;
        video.autoplay = true;
        return video;
    }

    // Reddit has renamed its audio files over the years; try current names first.
    const base = fallbackUrl.split('?')[0].replace(/\/[^/]+$/, '');
    const candidates = ['CMAF_AUDIO_128.mp4', 'CMAF_AUDIO_64.mp4', 'DASH_AUDIO_128.mp4', 'DASH_AUDIO_64.mp4', 'DASH_audio.mp4', 'audio']
        .map(name => `${base}/${name}`);
    if (!fallbackUrl.startsWith('https://v.redd.it/')) return video; // offline copy: no separate audio

    const audio = new Audio();
    audio.preload = 'auto';
    let next = 0;
    const tryNextAudio = () => {
        if (next < candidates.length) audio.src = candidates[next++];
        else audio.removeAttribute('src'); // genuinely silent video
    };
    audio.onerror = tryNextAudio;
    tryNextAudio();
    state.audios.push(audio);

    const syncTime = () => {
        if (audio.src && Math.abs(audio.currentTime - video.currentTime) > 0.25) audio.currentTime = video.currentTime;
    };
    const playAudio = () => {
        if (!audio.src || video.paused) return;
        syncTime();
        audio.play().catch(() => {});
    };
    video.addEventListener('play', playAudio);
    video.addEventListener('playing', playAudio);
    video.addEventListener('pause', () => audio.pause());
    video.addEventListener('waiting', () => audio.pause());
    video.addEventListener('ended', () => audio.pause());
    video.addEventListener('seeked', syncTime);
    video.addEventListener('timeupdate', syncTime);
    video.addEventListener('ratechange', () => { audio.playbackRate = video.playbackRate; });
    // 'waiting' doesn't always fire when the video runs dry, so also hold the audio whenever the
    // video can't actually advance, and resume once it can.
    const guard = setInterval(() => {
        if (!video.isConnected) {
            clearInterval(guard);
            audio.pause();
            return;
        }
        const starved = video.readyState < 3;
        if ((video.paused || starved) && !audio.paused) audio.pause();
        else if (!video.paused && !starved && audio.paused) playAudio();
    }, 250);
    video.addEventListener('volumechange', () => {
        audio.volume = video.volume;
        audio.muted = video.muted;
    });
    return video;
}

function image(src, list, index) {
    const img = el('img', { src, class: 'zoomable', alt: '', loading: 'lazy' });
    // The post's own media: the viewer can step on to the next post's pictures.
    img.onclick = () => openLightbox(list || [src], index || 0, true);
    return img;
}

function gallery(items) {
    const stills = items.map(it => it.src);
    let index = 0;
    const stage = el('div', { class: 'gallery-stage' });
    const counter = el('span', { class: 'gallery-counter' });
    const caption = el('div', { class: 'caption' });
    const show = n => {
        index = (n + items.length) % items.length;
        const item = items[index];
        stage.replaceChildren(item.video ? videoPlayer(null, item.video, null, true) : image(item.src, stills, index));
        counter.textContent = `${index + 1} / ${items.length}`;
        caption.textContent = item.caption || '';
    };
    const nav = items.length > 1 ? [
        el('button', { class: 'nav prev', title: 'Previous', onclick: () => show(index - 1) }, '‹'),
        el('button', { class: 'nav next', title: 'Next', onclick: () => show(index + 1) }, '›'),
        counter,
    ] : null;
    const node = el('div', { class: 'gallery' }, stage, nav, caption);
    show(0);
    return node;
}

function buildMedia(s) {
    const url = s.url_overridden_by_dest || s.url || '';

    if (s.is_gallery && s.gallery_data && s.media_metadata) {
        const items = s.gallery_data.items.map(it => {
            const m = s.media_metadata[it.media_id];
            if (!m || m.status !== 'valid' || !m.s) return null;
            if (m.e === 'AnimatedImage') return { video: mapUrl(m.s.mp4), src: mapUrl(m.s.gif || m.s.mp4), caption: it.caption };
            return { src: mapUrl(m.s.u || m.s.gif), caption: it.caption };
        }).filter(Boolean);
        if (items.length) return gallery(items);
    }

    const rv = (s.secure_media && s.secure_media.reddit_video) || (s.media && s.media.reddit_video) ||
        (s.preview && s.preview.reddit_video_preview);
    if (rv && rv.fallback_url) return redditVideo(mapUrl(rv.fallback_url), previewUrl(s), rv.is_gif);
    if (rv) return videoPlayer(rv.hls_url, null, previewUrl(s), rv.is_gif);

    if (/\.gifv(\?|$)/i.test(url)) {
        const mp4 = url.replace(/\.gifv/i, '.mp4');
        return videoPlayer(null, mapUrl(mp4), previewUrl(s), true);
    }
    if (/\.(mp4|webm)(\?|$)/i.test(url)) return videoPlayer(null, mapUrl(url), previewUrl(s), false);
    if (s.post_hint === 'image' || /\.(jpe?g|png|gif|webp)(\?|$)/i.test(url)) return image(mapUrl(url));

    const yt = youtubeId(url);
    if (yt) {
        return el('div', { class: 'embed' }, el('iframe', {
            src: `https://www.youtube-nocookie.com/embed/${yt}`,
            allow: 'autoplay; encrypted-media; picture-in-picture; fullscreen',
            allowfullscreen: true,
            referrerpolicy: 'strict-origin-when-cross-origin',
        }));
    }

    if (!s.is_self && url) {
        const preview = previewUrl(s);
        return el('a', { class: 'link-card', href: url },
            preview ? el('img', { src: preview, alt: '', loading: 'lazy' }) : null,
            el('div', null, el('div', { class: 'link-domain' }, s.domain || ''), el('div', { class: 'link-url' }, url)));
    }
    return null;
}

function mediaFor(source, post) {
    const content = buildMedia(source);
    if (!content) return null;
    const blur = state.settings.blurNsfw && (post.over_18 || post.spoiler);
    const wrap = el('div', { class: 'media' + (blur ? ' blurred' : '') }, content);
    if (blur) {
        const reveal = el('button', { class: 'reveal' }, post.over_18 ? 'NSFW: click to show' : 'Spoiler: click to show');
        reveal.onclick = e => {
            e.stopPropagation();
            wrap.classList.remove('blurred');
            reveal.remove();
        };
        wrap.append(reveal);
    }
    return wrap;
}

// ---- Rendering -------------------------------------------------------------------------

function clearView() {
    for (const h of state.hls) h.destroy();
    state.hls = [];
    for (const a of state.audios) {
        a.onerror = null; // stop it trying the next audio candidate for a post that's gone
        a.pause();
        a.removeAttribute('src');
        a.load(); // aborts the in-flight download
    }
    state.audios = [];
    // Overlays and find results belong to the old post.
    closeLightbox();
    closeMenu();
    hideHovercard();
    resetFind();
    state.newIndex = state.opIndex = -1;
    state.post = null;
    root.replaceChildren();
    empty.hidden = false;
    window.scrollTo(0, 0);
}

function showLoading() {
    empty.hidden = true;
    root.append(spinner());
}

function showError(message) {
    const box = el('div', { class: 'error' }, message);
    const comments = document.getElementById('comments');
    if (comments) comments.replaceChildren(box);
    else {
        empty.hidden = true;
        root.replaceChildren(box);
    }
}

function tagsLine(d) {
    const tags = [
        d.stickied && el('span', { class: 'tag pin' }, 'PINNED'),
        d.over_18 && el('span', { class: 'tag nsfw' }, 'NSFW'),
        d.spoiler && el('span', { class: 'tag spoiler' }, 'SPOILER'),
        d.is_original_content && el('span', { class: 'tag' }, 'OC'),
        d.locked && el('span', { class: 'tag' }, '🔒 LOCKED'),
        d.archived && el('span', { class: 'tag' }, 'ARCHIVED'),
        d.link_flair_text && el('span', { class: 'tag' }, d.link_flair_text),
    ].filter(Boolean);
    return tags.length ? el('div', { class: 'tags' }, tags) : null;
}

function renderPost(d, settings) {
    if (settings) state.settings = settings;
    if (state.post && state.post.name === d.name && root.querySelector('.post')) {
        state.post = d;
        return;
    }
    clearView();
    state.post = d;
    empty.hidden = true;

    // Crossposts carry their media and text on the original.
    const source = (d.crosspost_parent_list && d.crosspost_parent_list[0]) || d;
    const permalink = 'https://www.reddit.com' + d.permalink;
    const mine = !!state.settings.me && sameUser(d.author, state.settings.me);
    const modOn = isModOf(d.subreddit);
    const postBadges = modOn ? modBadges(d) : null;
    // An empty placeholder keeps a slot the editor can swap into for self posts with no text yet.
    let body = (d.selftext_html || source.selftext_html)
        ? mdNode(d.selftext_html || source.selftext_html, 'post-body', d.media_metadata || source.media_metadata)
        : el('div', { class: 'md post-body' });

    const box = replyBox(d.name, comment => {
        const list = document.getElementById('comments');
        list.querySelector('.no-comments')?.remove();
        list.prepend(commentNode(comment, 0, d));
        showToast('Reply posted');
    });

    const article = el('article', { class: 'post' },
        el('div', { class: 'post-head' },
            voteWidget(d, true),
            el('div', { class: 'post-titles' },
                el('h1', null, d.title),
                tagsLine(d),
                postBadges,
                el('div', { class: 'meta' },
                    link('r/' + d.subreddit, '/r/' + d.subreddit), ' · ',
                    link('u/' + d.author, '/user/' + d.author), d.author !== '[deleted]' && userTagChip(d.author), ' · ',
                    el('span', { title: fullDate(d.created_utc) }, age(d.created_utc)),
                    d.is_self ? null : [' · ', d.domain],
                    source !== d ? [' · crossposted from ', link('r/' + source.subreddit, '/r/' + source.subreddit)] : null))),
        mediaFor(source, d),
        body,
        modOn && reportList(d),
        el('div', { class: 'actions' },
            action('💬 Reply', () => box.open()),
            saveButton(d),
            hasDownloadableMedia(d) && action('⬇ Download', () => downloadPost(), 'Save images or video (d)'),
            action('↗ Open on Reddit', () => send({ type: 'external', url: permalink })),
            d.is_self ? null : action('🔗 Open link', () => send({ type: 'external', url: source.url || d.url })),
            action('📋 Copy link', () => navigator.clipboard.writeText(permalink).then(() => showToast('Link copied'))),
            modOn && el('button', {
                class: 'action menu-trigger mod-action', title: 'Moderator actions',
                onclick: e => openModMenu(e.currentTarget, d, 't3', postBadges, 0),
            }, '🛡 Mod'),
            mine && d.is_self && action('✏ Edit', () => openEditor(body, d.selftext || '',
                text => call('edit', { id: d.name, text }),
                data => {
                    d.selftext = data.selftext; // so a second edit starts from the saved text
                    return (body = mdNode(data.selftext_html || '', 'post-body', data.media_metadata));
                }, d.name)),
            mine && action('🗑 Delete', async () => {
                if (await deleteThing(d.name, 'post')) article.querySelector('h1').textContent = '[deleted] ' + d.title;
            })),
        box);

    root.append(article, commentsSection(d));
    continueViewer(d);
}

function commentsSection(d) {
    const select = el('select', { title: 'Sort comments' },
        COMMENT_SORTS.map(([value, label]) => el('option', { value, selected: value === state.sort }, label)));
    const list = el('div', { id: 'comments' }, spinner());
    select.onchange = () => {
        state.sort = select.value;
        list.replaceChildren(spinner());
        call('sort', { sort: select.value }).catch(err => showError(err.message));
    };
    return el('section', { class: 'comments' },
        el('div', { class: 'comments-head' },
            el('h2', null, `${count(d.num_comments || 0)} comments`),
            el('div', { class: 'head-tools' },
                el('button', {
                    class: 'action', title: 'Load the latest comments',
                    onclick: () => {
                        list.replaceChildren(spinner());
                        call('sort', { sort: state.sort }).catch(err => showError(err.message));
                    },
                }, '⟳ Refresh'),
                el('button', { class: 'action', title: 'Find in thread (Ctrl+F)', onclick: () => openFind() }, '🔍 Find'),
                el('button', { class: 'action', title: 'Next comment by the original poster (Shift+P)', onclick: () => nextOpComment() }, 'OP ↓'),
                select)),
        el('div', { id: 'new-note', class: 'note', hidden: true }),
        el('div', { id: 'focus-note', class: 'note', hidden: true }),
        list);
}

function renderThread(m) {
    if (m.settings) state.settings = m.settings;
    state.localMedia = m.localMedia || null;
    state.sort = m.sort || state.sort;
    state.lastVisit = m.lastVisit || null;
    const [postListing, commentListing] = m.data;
    const post = postListing.data.children[0].data;
    const alreadyShown = !!(state.post && state.post.name === post.name && root.querySelector('.post'));
    renderPost(post);
    if (alreadyShown) {
        // The header was drawn from the (older) listing copy; refresh what can have changed
        // without rebuilding it, so playing media isn't restarted.
        syncPostState({ id: post.name, likes: post.likes ?? null, score: post.score, saved: !!post.saved });
        const heading = root.querySelector('.comments-head h2');
        if (heading) heading.textContent = `${count(post.num_comments || 0)} comments`;
    }
    state.newIndex = state.opIndex = -1;

    const select = root.querySelector('.comments-head select');
    if (select) select.value = state.sort;

    const note = document.getElementById('focus-note');
    if (m.focus) {
        note.hidden = false;
        note.replaceChildren('You are viewing a single comment thread. ',
            el('button', {
                class: 'link',
                onclick: () => {
                    document.getElementById('comments').replaceChildren(spinner());
                    call('fullThread').catch(err => showError(err.message));
                },
            }, 'View all comments'));
    } else {
        note.hidden = true;
    }
    if (m.offline) showToast('Showing your offline copy');

    const list = document.getElementById('comments');
    const fragment = document.createDocumentFragment();
    renderChildren(commentListing.data.children, fragment, 0, post);
    list.replaceChildren(fragment);
    if (!list.children.length) list.append(el('p', { class: 'muted no-comments' }, 'No comments yet.'));

    const newCount = list.querySelectorAll('.comment.new').length;
    const newNote = document.getElementById('new-note');
    newNote.hidden = !newCount;
    if (newCount) {
        newNote.replaceChildren(
            el('b', null, `${newCount} new ${newCount === 1 ? 'comment' : 'comments'}`),
            ` since your last visit ${age(state.lastVisit)} ago · `,
            el('button', { class: 'link', onclick: () => nextNewComment() }, 'Jump to next new (Shift+N)'));
    }
    if (!findBar.hidden) runFind();

    if (m.focus) {
        const focused = document.getElementById('c_' + m.focus);
        if (focused) {
            focused.classList.add('focused');
            focused.scrollIntoView({ block: 'center' });
        }
    }
}

function renderChildren(children, container, depth, post) {
    for (const child of children) {
        if (child.kind === 't1') container.append(commentNode(child.data, depth, post));
        else if (child.kind === 'more') {
            const node = moreNode(child.data, depth, post);
            if (node) container.append(node);
        }
    }
}

function commentNode(c, depth, post) {
    const node = el('div', {
        class: 'comment', id: 'c_' + c.id, 'data-depth': depth, 'data-name': c.name, 'data-author': c.author,
    });
    const muted = isMuted(c.author);
    const isNew = !!state.lastVisit && c.created_utc > state.lastVisit && !sameUser(c.author, state.settings.me);
    node.classList.toggle('highlight', isHighlighted(c.author));
    node.classList.toggle('muted', muted);
    node.classList.toggle('new', isNew);
    node.classList.toggle('by-op', !!c.is_submitter);
    // Comments in the "more" stubs don't carry a subreddit; they belong to the post's.
    if (!c.subreddit && post) c.subreddit = post.subreddit;
    const modOn = isModOf(c.subreddit);
    const commentBadges = modOn ? modBadges(c) : null;
    const line = el('div', { class: 'thread-line', title: 'Collapse thread' });
    const toggle = el('button', { class: 'collapse', title: 'Collapse' }, '−');
    const hiddenCount = el('span', { class: 'muted' });
    const authorClass = 'plain author' + (c.is_submitter ? ' op' : '') + (c.distinguished ? ' ' + c.distinguished : '');

    const head = el('div', { class: 'c-head' },
        toggle,
        c.author === '[deleted]' ? el('span', { class: 'muted' }, '[deleted]') : link(c.author, '/user/' + c.author, authorClass),
        c.author !== '[deleted]' && userTagChip(c.author),
        c.is_submitter && el('span', { class: 'badge op' }, 'OP'),
        c.distinguished === 'moderator' && el('span', { class: 'badge mod' }, 'MOD'),
        c.distinguished === 'admin' && el('span', { class: 'badge admin' }, 'ADMIN'),
        c.stickied && el('span', { class: 'badge pin' }, 'PINNED'),
        isNew && el('span', { class: 'badge new' }, 'NEW'),
        el('span', { class: 'badge muted-badge' }, 'MUTED'),
        c.author_flair_text && el('span', { class: 'flair' }, c.author_flair_text),
        commentBadges,
        el('span', { class: 'muted' }, '· ' + (c.score_hidden ? 'score hidden' : count(c.score) + ' points') + ' ·'),
        el('span', { class: 'muted', title: fullDate(c.created_utc) }, age(c.created_utc)),
        c.edited && el('span', { class: 'muted' }, '· edited'),
        hiddenCount);

    const replies = el('div', { class: 'replies' });
    const box = replyBox(c.name, comment => {
        replies.prepend(commentNode(comment, depth + 1, post));
        showToast('Reply posted');
    });

    const mine = !!state.settings.me && sameUser(c.author, state.settings.me);
    let body = mdNode(c.body_html, 'c-body', c.media_metadata);

    const actions = el('div', { class: 'c-actions' },
        voteWidget(c, false),
        action('Reply', () => box.open()),
        saveButton(c),
        mine && action('Edit', () => openEditor(body, c.body || '',
            text => call('edit', { id: c.name, text }),
            data => {
                c.body = data.body;
                return (body = mdNode(data.body_html || '', 'c-body', data.media_metadata));
            }, c.name)),
        mine && action('Delete', async () => {
            // With the editor open the body is detached, and Cancel would bring it back.
            if (!body.isConnected) {
                showToast('Save or cancel your edit first');
                return;
            }
            if (!(await deleteThing(c.name, 'comment'))) return;
            const gone = mdNode('<p>[deleted]</p>', 'c-body');
            body.replaceWith(gone);
            body = gone;
        }),
        action('Permalink', () => send({ type: 'external', url: 'https://www.reddit.com' + c.permalink })),
        modOn && el('button', {
            class: 'action menu-trigger mod-action', title: 'Moderator actions',
            onclick: e => openModMenu(e.currentTarget, c, 't1', commentBadges, depth),
        }, '🛡 Mod'),
        c.author !== '[deleted]' &&
            el('button', { class: 'action menu-trigger', title: 'More', onclick: e => openUserMenu(e.currentTarget, c.author) }, '⋯'));

    if (c.replies && c.replies.data) renderChildren(c.replies.data.children, replies, depth + 1, post);

    node.append(line, el('div', { class: 'c-content' }, head, body, modOn && reportList(c), actions, box, replies));

    const collapse = () => {
        const collapsed = node.classList.toggle('collapsed');
        toggle.textContent = collapsed ? '+' : '−';
        const n = node.querySelectorAll('.replies .comment').length;
        hiddenCount.textContent = collapsed && n ? `(${n} ${n === 1 ? 'reply' : 'replies'} hidden)` : '';
    };
    node.toggleCollapse = collapse;
    line.onclick = collapse;
    toggle.onclick = collapse;

    const lowScore = state.settings.collapseBelow != null && !c.score_hidden && c.score <= state.settings.collapseBelow;
    if (c.collapsed || muted || lowScore) collapse();
    return node;
}

// ---- User menu (mute / highlight) ---------------------------------------------------------

function closeMenu() {
    const menu = document.getElementById('menu');
    if (menu) menu.remove();
}

function openUserMenu(anchor, name) {
    closeMenu();
    const muted = isMuted(name);
    const highlighted = isHighlighted(name);
    const menu = el('div', { id: 'menu' },
        el('button', { onclick: () => { closeMenu(); send({ type: 'open', url: 'https://www.reddit.com/user/' + name }); } },
            `View u/${name}`),
        el('button', { onclick: () => setUserPref(name, 'highlighted', !highlighted) },
            highlighted ? `Stop highlighting u/${name}` : `⭐ Highlight u/${name}`),
        el('button', { onclick: () => setUserPref(name, 'muted', !muted) },
            muted ? `Unmute u/${name}` : `🔇 Mute u/${name}`),
        !sameUser(name, state.settings.me) &&
            el('button', { onclick: () => { closeMenu(); send({ type: 'compose', to: name }); } }, `✉ Send u/${name} a message…`),
        el('button', { onclick: () => openNoteForm(menu, name) },
            noteFor(name) ? `📝 Edit your note on u/${name}…` : `📝 Note or tag on u/${name}…`));
    document.body.append(menu);
    const r = anchor.getBoundingClientRect();
    const w = menu.getBoundingClientRect();
    menu.style.left = Math.min(r.left, window.innerWidth - w.width - 8) + 'px';
    menu.style.top = (r.bottom + w.height + 8 > window.innerHeight ? r.top - w.height - 4 : r.bottom + 4) + 'px';
}

// ---- Your notes and tags on users -------------------------------------------------------------

const noteFor = name => (state.settings.notes || {})[(name || '').toLowerCase()] || null;

/** A chip after a username with your tag for them (kept in step by refreshUserTags). */
function userTagChip(name) {
    const chip = el('span', { class: 'user-tag', 'data-user': (name || '').toLowerCase() });
    paintUserTag(chip);
    return chip;
}

function paintUserTag(chip) {
    const note = noteFor(chip.dataset.user);
    chip.hidden = !note;
    chip.textContent = note ? (note.tag || '📝') : '';
    chip.title = note ? (note.note || 'Your tag for this user') : '';
}

function refreshUserTags() {
    for (const chip of root.querySelectorAll('.user-tag')) paintUserTag(chip);
    userCache.clear(); // hover cards show the note too
}

function openNoteForm(menu, name) {
    const current = noteFor(name) || { tag: '', note: '' };
    const tag = el('input', { type: 'text', maxlength: '40', placeholder: 'Tag, shown next to their name', value: current.tag });
    const note = el('textarea', { rows: '3', placeholder: 'Note (only you see it)', spellcheck: 'true' });
    note.value = current.note;
    const status = el('div', { class: 'reply-status' });
    const save = async (tagValue, noteValue) => {
        try {
            state.settings = await call('userNote', { name, tag: tagValue, note: noteValue });
            closeMenu();
            refreshUserTags();
            showToast(tagValue || noteValue ? `Saved your note on u/${name}` : `Cleared your note on u/${name}`);
        } catch (err) {
            status.textContent = err.message;
        }
    };
    menu.classList.add('form');
    menu.replaceChildren(el('div', { class: 'menu-title' }, `Your note on u/${name}`), tag, note,
        el('div', { class: 'reply-actions' },
            el('button', { class: 'primary', onclick: () => save(tag.value.trim(), note.value.trim()) }, 'Save'),
            el('button', { onclick: () => save('', '') }, 'Clear'),
            el('button', { onclick: () => closeMenu() }, 'Cancel')),
        status);
    const box = menu.getBoundingClientRect();
    if (box.bottom > window.innerHeight - 8) menu.style.top = Math.max(8, window.innerHeight - box.height - 8) + 'px';
    if (box.right > window.innerWidth - 8) menu.style.left = Math.max(8, window.innerWidth - box.width - 8) + 'px';
    tag.focus();
}

async function setUserPref(name, list, on) {
    closeMenu();
    try {
        state.settings = await call('userPref', { name, list, on });
        for (const node of root.querySelectorAll('.comment')) {
            const author = node.dataset.author;
            node.classList.toggle('highlight', isHighlighted(author));
            const muted = isMuted(author);
            if (muted && !node.classList.contains('muted') && !node.classList.contains('collapsed')) node.toggleCollapse();
            node.classList.toggle('muted', muted);
        }
        const verb = list === 'muted' ? (on ? 'Muted' : 'Unmuted') : (on ? 'Highlighting' : 'Stopped highlighting');
        showToast(`${verb} u/${name}`);
    } catch (err) {
        showToast(err.message);
    }
}

// ---- Moderation ----------------------------------------------------------------------------

const isModOf = sub => (state.settings.modOf || []).some(s => sameUser(s, sub));

/** Status badges (removed / approved / report count), refreshed after each mod action. */
function modBadges(thing) {
    const holder = el('span', { class: 'mod-badges' });
    holder.refresh = () => {
        const reports = (thing.num_reports || 0);
        holder.replaceChildren(
            thing.__removed && el('span', { class: 'badge removed' }, 'REMOVED'),
            thing.__approved && el('span', { class: 'badge approved' }, 'APPROVED'),
            !thing.__approved && reports > 0 && el('span', { class: 'badge reported' }, `${reports} REPORT${reports === 1 ? '' : 'S'}`),
            thing.locked && el('span', { class: 'badge locked' }, 'LOCKED'));
    };
    thing.__removed = thing.__removed ?? !!(thing.removed || thing.spam || thing.banned_by ||
        ['moderator', 'automod_filtered', 'reddit', 'anti_evil_ops'].includes(thing.removed_by_category));
    thing.__approved = thing.__approved ?? !!(thing.approved_by || thing.approved);
    holder.refresh();
    return holder;
}

/** Report reasons from users and moderators, as Reddit sends them to mods. */
function reportList(thing) {
    const user = (thing.user_reports || []).map(r => `${r[1] > 1 ? r[1] + '× ' : ''}${r[0] || 'no reason'}`);
    const mod = (thing.mod_reports || []).map(r => `${r[0] || 'no reason'} (u/${r[1]})`);
    if (!user.length && !mod.length) return null;
    return el('div', { class: 'reports' },
        el('b', null, '⚑ Reports: '),
        [...user, ...mod.map(m => 'mod: ' + m)].join(' · '));
}

/** The 🛡 Mod menu for a post (kind 't3') or comment ('t1'). */
function openModMenu(anchor, thing, kind, badges, depth) {
    closeMenu();
    const sub = thing.subreddit;
    const mine = sameUser(thing.author, state.settings.me);
    const item = (label, action) => el('button', { onclick: () => runModAction(action, thing, sub, badges) }, label);
    const menu = el('div', { id: 'menu' },
        item('✓ Approve', 'approve'),
        item('✖ Remove', 'remove'),
        item('🚫 Remove as spam', 'spam'),
        item(thing.locked ? '🔓 Unlock' : '🔒 Lock', thing.locked ? 'unlock' : 'lock'),
        kind === 't3' && item(thing.stickied ? '📌 Unpin' : '📌 Pin to top', thing.stickied ? 'unsticky' : 'sticky'),
        kind === 't3' && item(thing.over_18 ? 'Unmark NSFW' : 'Mark NSFW', thing.over_18 ? 'unnsfw' : 'nsfw'),
        kind === 't3' && item(thing.spoiler ? 'Unmark spoiler' : 'Mark spoiler', thing.spoiler ? 'unspoiler' : 'spoiler'),
        mine && item(thing.distinguished ? 'Undistinguish' : 'Distinguish as mod', thing.distinguished ? 'undistinguish' : 'distinguish'),
        mine && kind === 't1' && depth === 0 && item('📌 Distinguish and pin', 'sticky_comment'),
        item(thing.ignore_reports ? 'Stop ignoring reports' : 'Ignore future reports', thing.ignore_reports ? 'unignore_reports' : 'ignore_reports'),
        thing.author && thing.author !== '[deleted]' && !mine &&
            el('button', { onclick: () => openBanForm(menu, thing.author, sub) }, `⛔ Ban u/${thing.author}…`));
    document.body.append(menu);
    positionMenu(menu, anchor);
}

function positionMenu(menu, anchor) {
    const r = anchor.getBoundingClientRect();
    const w = menu.getBoundingClientRect();
    menu.style.left = Math.max(8, Math.min(r.left, window.innerWidth - w.width - 8)) + 'px';
    menu.style.top = (r.bottom + w.height + 8 > window.innerHeight ? Math.max(8, r.top - w.height - 4) : r.bottom + 4) + 'px';
}

async function runModAction(action, thing, sub, badges) {
    closeMenu();
    try {
        await call('mod', { action, id: thing.name, subreddit: sub });
        // Mirror the change locally so the menu and badges stay right without a reload.
        switch (action) {
            case 'approve': thing.__approved = true; thing.__removed = false; break;
            case 'remove': case 'spam': thing.__removed = true; thing.__approved = false; break;
            case 'lock': thing.locked = true; break;
            case 'unlock': thing.locked = false; break;
            case 'sticky': case 'sticky_comment': thing.stickied = true; if (action === 'sticky_comment') thing.distinguished = 'moderator'; break;
            case 'unsticky': thing.stickied = false; break;
            case 'nsfw': thing.over_18 = true; break;
            case 'unnsfw': thing.over_18 = false; break;
            case 'spoiler': thing.spoiler = true; break;
            case 'unspoiler': thing.spoiler = false; break;
            case 'distinguish': thing.distinguished = 'moderator'; break;
            case 'undistinguish': thing.distinguished = null; break;
            case 'ignore_reports': thing.ignore_reports = true; break;
            case 'unignore_reports': thing.ignore_reports = false; break;
        }
        if (badges) badges.refresh();
        const done = {
            approve: 'Approved', remove: 'Removed', spam: 'Removed as spam', lock: 'Locked', unlock: 'Unlocked',
            sticky: 'Pinned', unsticky: 'Unpinned', sticky_comment: 'Distinguished and pinned', nsfw: 'Marked NSFW',
            unnsfw: 'Unmarked NSFW', spoiler: 'Marked as spoiler', unspoiler: 'Unmarked spoiler',
            distinguish: 'Distinguished', undistinguish: 'Undistinguished',
            ignore_reports: 'Ignoring future reports', unignore_reports: 'No longer ignoring reports',
        };
        showToast(done[action] || 'Done');
    } catch (err) {
        showToast('Mod action failed: ' + err.message);
    }
}

function openBanForm(menu, user, sub) {
    const reason = el('input', { type: 'text', maxlength: '100', placeholder: 'Reason (shown to other mods)' });
    const days = el('input', { type: 'number', min: '1', max: '999', placeholder: 'Days (blank = permanent)' });
    const message = el('textarea', { rows: '3', placeholder: 'Message to the user (optional)', spellcheck: 'true' });
    const note = el('input', { type: 'text', maxlength: '300', placeholder: 'Mod note (optional)' });
    const status = el('div', { class: 'reply-status' });
    const ban = el('button', { class: 'danger' }, `Ban u/${user}`);
    ban.onclick = async () => {
        ban.disabled = true;
        status.textContent = 'Banning…';
        try {
            await call('ban', {
                subreddit: sub, user, days: parseInt(days.value, 10) || 0,
                reason: reason.value.trim(), message: message.value.trim(), note: note.value.trim(),
            });
            closeMenu();
            showToast(`Banned u/${user} from r/${sub}${parseInt(days.value, 10) ? ` for ${parseInt(days.value, 10)} days` : ''}`);
        } catch (err) {
            status.textContent = err.message;
            ban.disabled = false;
        }
    };
    menu.classList.add('form');
    menu.replaceChildren(el('div', { class: 'menu-title' }, `Ban u/${user} from r/${sub}`), reason, days, message, note,
        el('div', { class: 'reply-actions' }, ban, el('button', { onclick: () => closeMenu() }, 'Cancel')), status);
    // The form is taller than the menu it replaced; keep it on screen.
    const box = menu.getBoundingClientRect();
    if (box.bottom > window.innerHeight - 8) menu.style.top = Math.max(8, window.innerHeight - box.height - 8) + 'px';
    if (box.right > window.innerWidth - 8) menu.style.left = Math.max(8, window.innerWidth - box.width - 8) + 'px';
    reason.focus();
}

// ---- New since last visit -------------------------------------------------------------------

function expandAncestors(node) {
    for (let c = node && node.closest('.comment'); c; c = c.parentElement && c.parentElement.closest('.comment')) {
        if (c.classList.contains('collapsed') && c.toggleCollapse) c.toggleCollapse();
    }
}

/**
 * Steps through the comments matching a selector, in thread order, opening collapsed threads on
 * the way. (Picking "the first one below the viewport top" re-picked the comment just centred,
 * and skipped ones inside collapsed threads.) The position is kept in state[key].
 */
function jumpThrough(selector, key, noneMessage) {
    const matches = [...root.querySelectorAll(selector)];
    if (!matches.length) {
        showToast(noneMessage);
        return;
    }
    state[key] = ((state[key] ?? -1) + 1) % matches.length;
    const next = matches[state[key]];
    expandAncestors(next);
    next.scrollIntoView({ block: 'center', behavior: 'smooth' });
    next.classList.remove('flash');
    void next.offsetWidth; // restart the animation
    next.classList.add('flash');
    if (matches.length > 1) showToast(`${state[key] + 1} of ${matches.length}`);
}

function nextNewComment() {
    jumpThrough('.comment.new', 'newIndex', 'No new comments since your last visit');
}

/** The original poster's own comments: often the answers in a question thread. */
function nextOpComment() {
    jumpThrough('.comment.by-op', 'opIndex', 'The original poster hasn\'t commented here');
}

// ---- Find in thread ---------------------------------------------------------------------

const findInput = el('input', { type: 'search', placeholder: 'Find in thread…', spellcheck: 'false' });
const findCount = el('span', { class: 'find-count' });
const findLoadAll = el('button', { title: 'Load every collapsed "more replies" so search covers the whole thread' }, 'Load all');
const findBar = el('div', { id: 'findbar', hidden: true },
    findInput, findCount,
    el('button', { title: 'Previous (Shift+Enter)', onclick: () => stepFind(-1) }, '↑'),
    el('button', { title: 'Next (Enter)', onclick: () => stepFind(1) }, '↓'),
    findLoadAll,
    el('button', { title: 'Close (Esc)', onclick: () => closeFind() }, '✕'));
document.body.append(findBar);

let findRanges = [];
let findIndex = -1;
let findTimer;

function openFind() {
    findBar.hidden = false;
    findInput.focus();
    findInput.select();
    if (findInput.value) runFind();
}

function closeFind() {
    findBar.hidden = true;
    resetFind();
}

/** Drops matches (they point into the old DOM); the bar keeps its text for the next search. */
function resetFind() {
    findRanges = [];
    findIndex = -1;
    findCount.textContent = '';
    if (window.CSS && CSS.highlights) {
        CSS.highlights.delete('find');
        CSS.highlights.delete('find-current');
    }
}

function runFind() {
    const q = findInput.value.trim().toLowerCase();
    findRanges = [];
    if (q) {
        // Only the outer body wrappers: Reddit's HTML nests its own <div class="md"> inside ours,
        // which counted every match twice, and reply-box previews aren't part of the thread.
        for (const scope of root.querySelectorAll('.post h1, .post-body, .c-body')) {
            const walker = document.createTreeWalker(scope, NodeFilter.SHOW_TEXT);
            for (let n = walker.nextNode(); n; n = walker.nextNode()) {
                const text = n.nodeValue.toLowerCase();
                for (let i = text.indexOf(q); i !== -1; i = text.indexOf(q, i + q.length)) {
                    const range = new Range();
                    range.setStart(n, i);
                    range.setEnd(n, i + q.length);
                    findRanges.push(range);
                }
            }
        }
    }
    if (window.CSS && CSS.highlights) CSS.highlights.set('find', new Highlight(...findRanges));
    findIndex = findRanges.length ? 0 : -1;
    showFindCurrent();
}

function stepFind(delta) {
    if (!findRanges.length) return;
    findIndex = (findIndex + delta + findRanges.length) % findRanges.length;
    showFindCurrent();
}

function showFindCurrent() {
    findCount.textContent = findRanges.length
        ? `${findIndex + 1} / ${findRanges.length}`
        : findInput.value.trim() ? 'No matches' : '';
    if (!(window.CSS && CSS.highlights)) return;
    if (findIndex < 0) {
        CSS.highlights.delete('find-current');
        return;
    }
    const range = findRanges[findIndex];
    expandAncestors(range.startContainer.parentElement);
    CSS.highlights.set('find-current', new Highlight(range));
    const rect = range.getBoundingClientRect();
    window.scrollBy({ top: rect.top - window.innerHeight / 3, behavior: 'smooth' });
}

/** Expands every "load more replies" (not separate deep threads) so search sees everything. */
async function loadAllComments() {
    findLoadAll.disabled = true;
    try {
        for (let round = 1; round <= 15; round++) {
            const buttons = [...root.querySelectorAll('.more button[data-more]')].filter(b => !b.disabled);
            if (!buttons.length) break;
            findLoadAll.textContent = `Loading… (${round})`;
            for (const button of buttons) await button.loadMore();
        }
    } finally {
        findLoadAll.textContent = 'Load all';
        findLoadAll.disabled = false;
    }
    runFind();
    const deep = root.querySelectorAll('.more button[data-continue]').length;
    if (deep) showToast(`${deep} deeper ${deep === 1 ? 'thread opens' : 'threads open'} separately and aren't searched`);
}

findLoadAll.onclick = () => loadAllComments();
findInput.addEventListener('input', () => {
    clearTimeout(findTimer);
    findTimer = setTimeout(runFind, 150);
});
findInput.addEventListener('keydown', e => {
    if (e.key === 'Enter') {
        stepFind(e.shiftKey ? -1 : 1);
        e.preventDefault();
    } else if (e.key === 'Escape') {
        closeFind();
        e.preventDefault();
        e.stopPropagation();
    }
});

function moreNode(m, depth, post) {
    const parentId = (m.parent_id || '').slice(3);

    // "Continue this thread": Reddit wants a fresh request rooted at the parent comment.
    if (m.id === '_' || (!m.count && (!m.children || !m.children.length))) {
        return el('div', { class: 'more' }, el('button', {
            class: 'link',
            'data-continue': true,
            onclick: () => send({ type: 'open', url: `https://www.reddit.com/comments/${post.id}/_/${parentId}` }),
        }, 'Continue this thread →'));
    }
    if (!m.children || !m.children.length) return null;

    const n = Math.max(m.count, m.children.length);
    const button = el('button', { class: 'link', 'data-more': true }, `Load ${n} more ${n === 1 ? 'reply' : 'replies'}`);
    const wrap = el('div', { class: 'more' }, button);

    button.onclick = () => button.loadMore();
    button.loadMore = async () => {
        if (button.disabled) return;
        button.disabled = true;
        button.textContent = 'Loading…';
        try {
            const batch = m.children.slice(0, 100);
            const rest = m.children.slice(100);
            const result = await call('more', { linkId: post.name, children: batch });
            const things = (result && result.json && result.json.data && result.json.data.things) || [];

            // Results come back flat; rebuild the tree using parent ids.
            const fragment = document.createDocumentFragment();
            const created = new Map();
            for (const thing of things) {
                const parentName = thing.data.parent_id;
                const parent = created.get(parentName) || root.querySelector(`.comment[data-name="${parentName}"]`);
                const isSibling = parentName === m.parent_id;
                const childDepth = isSibling ? depth : parent ? Number(parent.dataset.depth) + 1 : depth;
                const node = thing.kind === 't1'
                    ? commentNode(thing.data, childDepth, post)
                    : moreNode(thing.data, childDepth, post);
                if (!node) continue;
                if (thing.kind === 't1') created.set(thing.data.name, node);

                if (isSibling || !parent) fragment.append(node);
                else parent.querySelector(':scope > .c-content > .replies').append(node);
            }
            if (rest.length) {
                const remaining = moreNode({ ...m, children: rest, count: Math.max(rest.length, m.count - batch.length) }, depth, post);
                if (remaining) fragment.append(remaining);
            }
            wrap.replaceWith(fragment);
        } catch (err) {
            button.disabled = false;
            button.textContent = 'Retry loading replies';
            showToast(err.message);
        }
    };
    return wrap;
}

function renderMessage(d, settings) {
    clearView();
    if (settings) state.settings = settings;
    empty.hidden = true;
    const box = replyBox(d.name, () => showToast('Reply sent'));
    const from = d.author
        ? link('u/' + d.author, '/user/' + d.author)
        : (d.subreddit_name_prefixed || 'Reddit');
    root.append(el('article', { class: 'post' },
        el('h1', null, d.subject || 'Message'),
        el('div', { class: 'meta' }, 'from ', from, ' · ', el('span', { title: fullDate(d.created_utc) }, age(d.created_utc))),
        mdNode(d.body_html, 'post-body', d.media_metadata),
        el('div', { class: 'actions' }, action('💬 Reply', () => box.open())),
        box));
}

/**
 * A private message conversation: every message in order, yours on the right, with a reply
 * box (answering the latest message) at the end.
 */
function renderConversation(m) {
    if (m.settings) state.settings = m.settings;
    const children = (m.data && m.data.data && m.data.data.children) || [];
    if (!children.length) return;
    const first = children[0].data;
    const replies = (first.replies && first.replies.data && first.replies.data.children) || [];
    const messages = [first, ...replies.map(r => r.data)].sort((a, b) => a.created_utc - b.created_utc);
    if (messages.length < 2) return; // nothing more than the message already showing

    clearView();
    empty.hidden = true;
    const others = [...new Set(messages.map(x => x.author).filter(a => a && !sameUser(a, state.settings.me)))];
    const list = el('div', { class: 'conversation' });
    const bubble = x => {
        const mine = sameUser(x.author, state.settings.me);
        return el('div', { class: 'bubble' + (mine ? ' mine' : '') + (x.name === m.focus ? ' focused' : ''), id: 'm_' + x.id },
            el('div', { class: 'meta' },
                mine ? 'You' : (x.author ? link('u/' + x.author, '/user/' + x.author) : (x.subreddit_name_prefixed || 'Reddit')),
                ' · ', el('span', { title: fullDate(x.created_utc) }, age(x.created_utc))),
            mdNode(x.body_html, 'post-body', x.media_metadata));
    };
    for (const x of messages) list.append(bubble(x));

    const last = messages[messages.length - 1];
    const box = replyBox(last.name, sent => {
        list.append(bubble(sent));
        showToast('Reply sent');
        list.lastElementChild.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
    });
    root.append(el('article', { class: 'post' },
        el('h1', null, first.subject || 'Conversation'),
        el('div', { class: 'meta' },
            others.length ? ['with ', others.map((o, i) => [i ? ', ' : '', link('u/' + o, '/user/' + o)])] : 'Conversation',
            ` · ${messages.length} messages`),
        list,
        el('div', { class: 'actions' }, action('💬 Reply', () => box.open())),
        box));
    const focused = list.querySelector('.bubble.focused');
    if (focused) focused.scrollIntoView({ block: 'center' });
}

// ---- Display and navigation ------------------------------------------------------------

function applyDisplay(m) {
    document.documentElement.style.setProperty('--font-size', m.fontSize + 'px');
    document.body.classList.toggle('reading', !!m.reading);
}

/** Jump to the next/previous top-level comment relative to the top of the viewport. */
function navigateComments(direction) {
    const list = document.getElementById('comments');
    if (!list) return;
    const tops = [...list.children].filter(n => n.classList.contains('comment'));
    if (!tops.length) return;
    const threshold = 12;
    let target;
    if (direction > 0) target = tops.find(n => n.getBoundingClientRect().top > threshold);
    else target = [...tops].reverse().find(n => n.getBoundingClientRect().top < -threshold);
    if (!target) return;
    target.scrollIntoView({ block: 'start', behavior: 'smooth' });
    target.classList.remove('flash');
    void target.offsetWidth; // restart the animation
    target.classList.add('flash');
}

// ---- Hover cards: user profiles and image links -----------------------------------------

const hovercard = el('div', { id: 'hovercard', hidden: true });
document.body.append(hovercard);
const userCache = new Map();
let hoverTimer = null;
let hoverTarget = null;

const IMAGE_LINK = /^https?:\/\/(i\.redd\.it|preview\.redd\.it|i\.imgur\.com|[^/]+)\/\S+\.(jpe?g|png|gif|webp)(\?\S*)?$/i;

function imageLinkUrl(href) {
    if (IMAGE_LINK.test(href)) return href;
    // imgur page links (imgur.com/abc123) have a direct .jpg twin.
    const imgur = /^https?:\/\/(?:www\.)?imgur\.com\/([A-Za-z0-9]{5,8})$/.exec(href);
    return imgur ? `https://i.imgur.com/${imgur[1]}.jpg` : null;
}

function placeHovercard(anchor) {
    const r = anchor.getBoundingClientRect();
    hovercard.hidden = false;
    const cardRect = hovercard.getBoundingClientRect();
    let left = Math.min(r.left, window.innerWidth - cardRect.width - 12);
    let top = r.bottom + 8;
    if (top + cardRect.height > window.innerHeight - 8) top = Math.max(8, r.top - cardRect.height - 8);
    hovercard.style.left = Math.max(8, left) + 'px';
    hovercard.style.top = top + 'px';
}

async function showUserCard(anchor, name) {
    let info = userCache.get(name);
    if (!info) {
        hovercard.replaceChildren(el('div', { class: 'hc-loading' }, 'u/' + name + '…'));
        placeHovercard(anchor);
        try {
            info = await call('userInfo', { name });
            userCache.set(name, info);
        } catch {
            hideHovercard();
            return;
        }
    }
    if (hoverTarget !== anchor || !info) return;
    hovercard.replaceChildren(
        el('div', { class: 'hc-user' },
            info.icon ? el('img', { src: info.icon, class: 'hc-avatar', alt: '' }) : el('div', { class: 'hc-avatar blank' }),
            el('div', null,
                el('div', { class: 'hc-name' }, 'u/' + info.name),
                el('div', { class: 'muted' }, info.suspended ? 'Suspended account' : `Redditor for ${age(info.created)}`))),
        info.suspended ? null : el('div', { class: 'hc-stats' },
            el('div', null, el('b', null, count(info.karma)), el('span', { class: 'muted' }, ' karma')),
            el('div', null, el('b', null, count(info.linkKarma)), el('span', { class: 'muted' }, ' post')),
            el('div', null, el('b', null, count(info.commentKarma)), el('span', { class: 'muted' }, ' comment'))),
        noteFor(name) && el('div', { class: 'hc-note' },
            noteFor(name).tag && el('span', { class: 'user-tag' }, noteFor(name).tag),
            noteFor(name).note && el('div', null, noteFor(name).note)));
    placeHovercard(anchor);
}

function showImageCard(anchor, src) {
    const img = el('img', { src, class: 'hc-image', alt: '' });
    img.onload = () => { if (hoverTarget === anchor) placeHovercard(anchor); };
    img.onerror = () => hideHovercard();
    hovercard.replaceChildren(img);
    placeHovercard(anchor);
}

function hideHovercard() {
    clearTimeout(hoverTimer);
    hoverTarget = null;
    hovercard.hidden = true;
}

document.addEventListener('mouseover', e => {
    const anchor = e.target.closest('a[href]');
    if (!anchor || anchor === hoverTarget) return;
    const href = anchor.getAttribute('href');
    const user = /^\/u(?:ser)?\/([\w-]+)\/?$/.exec(href);
    const resolved = (() => { try { return new URL(href, 'https://www.reddit.com').href; } catch { return ''; } })();
    const image = !user && imageLinkUrl(resolved);
    if (!user && !image) return;

    clearTimeout(hoverTimer);
    hoverTarget = anchor;
    hoverTimer = setTimeout(() => {
        if (hoverTarget !== anchor) return;
        if (user) showUserCard(anchor, user[1]);
        else showImageCard(anchor, image);
    }, 450);
});

document.addEventListener('mouseout', e => {
    if (!hoverTarget) return;
    const to = e.relatedTarget;
    if (to && (hoverTarget.contains(to) || hovercard.contains(to))) return;
    if (e.target.closest('a[href]') === hoverTarget || hovercard.contains(e.target)) hideHovercard();
});

window.addEventListener('scroll', hideHovercard, { passive: true });

// ---- Lightbox --------------------------------------------------------------------------

// The post's own pictures open as a viewer: stepping past the last one moves on to the next post
// in the list that has pictures (and the slideshow does that by itself). Pictures from comments
// stay a simple loop.

const lightbox = document.getElementById('lightbox');
const lightboxImg = lightbox.querySelector('img');
const lightboxCounter = lightbox.querySelector('.lb-counter');
const lightboxTitle = lightbox.querySelector('.lb-title');
const playButton = lightbox.querySelector('.lb-play');
const fullButton = lightbox.querySelector('.lb-full');
let lightboxList = [];
let lightboxIndex = 0;

const SLIDE_SECONDS = 5;
const viewer = { browsing: false, pending: 0, timer: null, fullscreen: false };

/** Full-size pictures of a post (gallery, or a single image), for the viewer. */
function postImages(d) {
    const s = (d.crosspost_parent_list && d.crosspost_parent_list[0]) || d;
    const url = s.url_overridden_by_dest || s.url || '';
    if (s.is_gallery && s.gallery_data && s.media_metadata) {
        const pics = s.gallery_data.items
            .map(it => s.media_metadata[it.media_id])
            .filter(m => m && m.status === 'valid' && m.s)
            .map(m => mapUrl(m.s.u || m.s.gif));
        if (pics.length) return pics;
    }
    if (s.is_video) return [];
    if (s.post_hint === 'image' || /\.(jpe?g|png|gif|webp)(\?|$)/i.test(url)) return [mapUrl(url)];
    return [];
}

function openLightbox(list, index, browsing = false) {
    lightboxList = list;
    lightboxIndex = index;
    viewer.browsing = browsing;
    lightbox.classList.remove('zoomed', 'loading');
    lightbox.hidden = false;
    lightboxTitle.textContent = browsing && state.post ? `${state.post.title}  ·  r/${state.post.subreddit}` : '';
    playButton.hidden = !browsing && list.length < 2;
    paintLightbox();
}

function paintLightbox() {
    lightboxImg.src = lightboxList[lightboxIndex];
    const many = lightboxList.length > 1;
    lightboxCounter.textContent = many ? `${lightboxIndex + 1} / ${lightboxList.length}` : '';
    lightbox.querySelector('.lb-prev').hidden = !many && !viewer.browsing;
    lightbox.querySelector('.lb-next').hidden = !many && !viewer.browsing;
}

function stepLightbox(delta) {
    const next = lightboxIndex + delta;
    if (viewer.browsing && (next < 0 || next >= lightboxList.length)) {
        stepPost(delta);
        return;
    }
    lightboxIndex = (next + lightboxList.length) % lightboxList.length;
    lightbox.classList.remove('zoomed');
    paintLightbox();
}

/** Past the last (or before the first) picture: on to the next post with pictures. */
async function stepPost(delta) {
    if (viewer.pending) return;
    viewer.pending = delta;
    lightbox.classList.add('loading');
    let found = false;
    try {
        found = await call('mediaStep', { direction: delta });
    } catch {
        found = false;
    }
    // On success the new post has already arrived and reopened the viewer (see renderPost).
    if (!found && viewer.pending) {
        viewer.pending = 0;
        lightbox.classList.remove('loading');
        stopSlideshow();
        showToast(delta > 0 ? 'No more pictures in this feed' : 'No earlier posts with pictures');
    }
}

/** Called after a post renders: reopen the viewer on it if we were stepping through posts. */
function continueViewer(d) {
    if (!viewer.pending) return;
    const direction = viewer.pending;
    viewer.pending = 0;
    const pics = postImages(d);
    if (pics.length) openLightbox(pics, direction > 0 ? 0 : pics.length - 1, true);
    else stepPost(direction); // Skein only picks posts with pictures, but just in case
}

function startSlideshow() {
    stopSlideshow();
    viewer.timer = setInterval(() => {
        if (lightbox.hidden && !viewer.pending) stopSlideshow();
        else if (!viewer.pending) stepLightbox(1);
    }, SLIDE_SECONDS * 1000);
    playButton.textContent = '❚❚';
    playButton.classList.add('on');
    showToast(`Slideshow: a new picture every ${SLIDE_SECONDS} seconds (Space to stop)`);
}

function stopSlideshow() {
    clearInterval(viewer.timer);
    viewer.timer = null;
    playButton.textContent = '▶';
    playButton.classList.remove('on');
}

function setFullscreen(on) {
    viewer.fullscreen = on;
    viewer.ownsFullscreen = on; // closing the viewer leaves full screen only if the viewer chose it
    fullButton.classList.toggle('on', on);
    send({ type: 'fullscreen', on });
}

/** Hides the overlay (switching posts does this too, mid-browse). */
function closeLightbox() {
    lightbox.hidden = true;
    lightboxImg.removeAttribute('src');
}

/** You closed the viewer: stop the slideshow and leave full screen as well. */
function exitViewer() {
    closeLightbox();
    stopSlideshow();
    viewer.pending = 0;
    viewer.browsing = false;
    if (viewer.fullscreen && viewer.ownsFullscreen) setFullscreen(false);
}

lightbox.addEventListener('click', e => {
    if (e.target === lightboxImg) lightbox.classList.toggle('zoomed');
    else if (e.target.closest('.lb-prev')) stepLightbox(-1);
    else if (e.target.closest('.lb-next')) stepLightbox(1);
    else if (e.target.closest('.lb-play')) viewer.timer ? stopSlideshow() : startSlideshow();
    else if (e.target.closest('.lb-full')) setFullscreen(!viewer.fullscreen);
    else if (e.target.closest('.lb-bar')) return;
    else exitViewer();
});

// ---- Global input ----------------------------------------------------------------------

function openUrl(href) {
    if (!href || href.startsWith('#')) return;
    let url;
    try {
        // Reddit's HTML uses site-relative links like /r/pics.
        url = new URL(href, 'https://www.reddit.com');
    } catch {
        return;
    }
    send({ type: 'open', url: url.href });
}

document.addEventListener('click', e => {
    if (!e.target.closest('#menu, .menu-trigger')) closeMenu();

    const spoiler = e.target.closest('.md-spoiler-text');
    if (spoiler && !spoiler.classList.contains('revealed')) {
        spoiler.classList.add('revealed');
        e.preventDefault();
        return;
    }

    const inlineImage = e.target.closest('img.md-img');
    if (inlineImage) {
        openLightbox([inlineImage.src], 0);
        return;
    }

    const anchor = e.target.closest('a[href]');
    if (!anchor) return;
    e.preventDefault();
    openUrl(anchor.getAttribute('href'));
});

document.addEventListener('auxclick', e => {
    const anchor = e.target.closest('a[href]');
    if (!anchor || e.button !== 1) return;
    e.preventDefault();
    openUrl(anchor.getAttribute('href'));
});

// ---- Middle-click auto-scroll ----------------------------------------------------------------
// Press and hold the wheel button and move to scroll (release to stop), or click it once to keep
// scrolling until the next click, key or wheel turn. Links keep middle-click for "open".

const autoScroll = { active: false, originY: 0, y: 0, moved: false, swallowClick: false };
const autoScrollMarker = el('div', { id: 'autoscroll', hidden: true }, '⇕');
document.body.append(autoScrollMarker);

function startAutoScroll(e) {
    Object.assign(autoScroll, { active: true, originY: e.clientY, y: e.clientY, moved: false });
    autoScrollMarker.style.left = (e.clientX - 15) + 'px';
    autoScrollMarker.style.top = (e.clientY - 15) + 'px';
    autoScrollMarker.hidden = false;
    document.body.classList.add('autoscrolling');
    requestAnimationFrame(autoScrollFrame);
}

function stopAutoScroll() {
    autoScroll.active = false;
    autoScrollMarker.hidden = true;
    document.body.classList.remove('autoscrolling');
}

function autoScrollFrame() {
    if (!autoScroll.active) return;
    const dy = autoScroll.y - autoScroll.originY;
    if (Math.abs(dy) > 10) {
        autoScroll.moved = true;
        window.scrollBy(0, Math.min(120, (Math.abs(dy) - 10) * 0.12) * Math.sign(dy));
    }
    requestAnimationFrame(autoScrollFrame);
}

document.addEventListener('mousedown', e => {
    if (autoScroll.active) {
        // The click that ends auto-scroll shouldn't also open a link or press a button.
        stopAutoScroll();
        autoScroll.swallowClick = true;
        e.preventDefault();
        return;
    }
    if (e.button !== 1) return;
    if (e.target.closest('a[href], video, iframe, textarea, input, select')) return;
    e.preventDefault(); // otherwise Chromium's own auto-scroll may start as well
    startAutoScroll(e);
}, true);
document.addEventListener('click', e => {
    if (!autoScroll.swallowClick) return;
    autoScroll.swallowClick = false;
    e.preventDefault();
    e.stopPropagation();
}, true);
document.addEventListener('mousemove', e => { if (autoScroll.active) autoScroll.y = e.clientY; });
document.addEventListener('mouseup', e => {
    if (autoScroll.active && e.button === 1 && autoScroll.moved) stopAutoScroll();
});
window.addEventListener('wheel', () => { if (autoScroll.active) stopAutoScroll(); }, { passive: true });
window.addEventListener('blur', () => { if (autoScroll.active) stopAutoScroll(); });

const NATIVE_KEYS = new Set([' ', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End']);

document.addEventListener('keydown', e => {
    if (autoScroll.active) {
        stopAutoScroll();
        if (e.key === 'Escape') {
            e.preventDefault();
            return;
        }
    }
    if (!lightbox.hidden) {
        // Only claim the lightbox's own keys; Ctrl+C and friends keep working.
        if (e.key === 'Escape') exitViewer();
        else if (e.key === 'ArrowRight' || e.key === 'ArrowDown' || e.key === 'PageDown') stepLightbox(1);
        else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp' || e.key === 'PageUp') stepLightbox(-1);
        else if (e.key === ' ' && (viewer.browsing || lightboxList.length > 1)) viewer.timer ? stopSlideshow() : startSlideshow();
        else if (e.key === 'F11') setFullscreen(!viewer.fullscreen);
        else return;
        e.preventDefault();
        return;
    }
    if (e.ctrlKey && !e.shiftKey && e.key.toLowerCase() === 'f') {
        openFind();
        e.preventDefault();
        return;
    }
    if (e.target.closest && e.target.closest('textarea, input, select, [contenteditable]')) return;
    if (e.altKey) return;
    if (e.key === 'Escape' && !findBar.hidden) {
        closeFind();
        return;
    }
    if (e.key === 'Escape' && document.getElementById('menu')) {
        closeMenu();
        return;
    }
    if (!e.ctrlKey && e.key === 'N') {
        nextNewComment();
        e.preventDefault();
        return;
    }
    if (!e.ctrlKey && e.key === 'P') {
        nextOpComment();
        e.preventDefault();
        return;
    }

    // Comment navigation is handled right here; no round trip needed.
    if (!e.ctrlKey && (e.key === 'J' || e.key === 'K')) {
        navigateComments(e.key === 'J' ? 1 : -1);
        e.preventDefault();
        return;
    }
    if (e.key === 'Escape') {
        hideHovercard();
        send({ type: 'key', key: 'Escape', ctrl: false, shift: false });
        return;
    }
    if (!e.ctrlKey && NATIVE_KEYS.has(e.key)) return; // let the page scroll itself
    if (e.ctrlKey && ['c', 'a', 'f'].includes(e.key.toLowerCase())) return;

    // Forward shortcuts so they work no matter which pane has focus.
    if (e.key.length === 1 || e.key === 'F5' || e.key === 'F11' || (e.ctrlKey && e.key === 'Tab')) {
        send({ type: 'key', key: e.key, ctrl: e.ctrlKey, shift: e.shiftKey });
        e.preventDefault();
    }
});

send({ type: 'ready' });
