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
    const textarea = el('textarea', { placeholder });
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

function sanitize(html) {
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
                if (name.startsWith('on') || name === 'style' || name === 'srcdoc') child.removeAttribute(attr.name);
                else if ((name === 'href' || name === 'src') && /^\s*(javascript|data|vbscript):/i.test(attr.value))
                    child.removeAttribute(attr.name);
            }
            walk(child);
        }
    };
    walk(tpl.content);

    // Image links Reddit renders as plain URLs in comments become inline images.
    for (const a of tpl.content.querySelectorAll('a[href]')) {
        const href = a.getAttribute('href');
        if (/^https:\/\/(preview|i)\.redd\.it\/\S+\.(jpe?g|png|gif|webp)/i.test(href)) {
            a.replaceWith(el('img', { src: mapUrl(href), class: 'md-img', loading: 'lazy', alt: '' }));
        }
    }
    return tpl.content;
}

function mdNode(html, cls) {
    const node = el('div', { class: 'md ' + (cls || '') });
    node.append(sanitize(html));
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

    const vote = async target => {
        const previous = likes;
        const previousValue = value;
        value += weight(target) - weight(likes);
        likes = target;
        paint();
        try {
            await call('vote', { id: thing.name, dir: weight(target) });
        } catch (err) {
            likes = previous;
            value = previousValue;
            paint();
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

// ---- Replies ---------------------------------------------------------------------------

function replyBox(parentName, onPosted) {
    const { textarea, preview } = markdownEditor('', 'Write a reply. Markdown is supported. Ctrl+Enter sends.');
    const status = el('span', { class: 'reply-status' });
    const submit = el('button', { class: 'primary' }, 'Reply');
    const cancel = el('button', null, 'Cancel');
    const box = el('div', { class: 'reply-box', hidden: true },
        textarea, el('div', { class: 'reply-actions' }, submit, cancel, status), preview);

    cancel.onclick = () => { box.hidden = true; };
    submit.onclick = async () => {
        const text = textarea.value.trim();
        if (!text) return;
        submit.disabled = true;
        status.textContent = 'Posting…';
        try {
            const comment = await call('comment', { parent: parentName, text });
            textarea.value = '';
            textarea.dispatchEvent(new Event('input'));
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
function openEditor(target, initial, save, render) {
    const { textarea, preview } = markdownEditor(initial, 'Edit. Markdown is supported. Ctrl+Enter saves.');
    const status = el('span', { class: 'reply-status' });
    const saveButton = el('button', { class: 'primary' }, 'Save edit');
    const cancel = el('button', null, 'Cancel');
    const editor = el('div', { class: 'reply-box' }, textarea, el('div', { class: 'reply-actions' }, saveButton, cancel, status), preview);
    target.replaceWith(editor);
    textarea.focus();

    cancel.onclick = () => editor.replaceWith(target);
    saveButton.onclick = async () => {
        saveButton.disabled = true;
        status.textContent = 'Saving…';
        try {
            const data = await save(textarea.value);
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
    if (!state.post) return;
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
    img.onclick = () => openLightbox(list || [src], index || 0);
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
        a.pause();
        a.removeAttribute('src');
    }
    state.audios = [];
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
    // An empty placeholder keeps a slot the editor can swap into for self posts with no text yet.
    let body = (d.selftext_html || source.selftext_html)
        ? mdNode(d.selftext_html || source.selftext_html, 'post-body')
        : el('div', { class: 'md post-body' });

    const box = replyBox(d.name, comment => {
        const list = document.getElementById('comments');
        list.prepend(commentNode(comment, 0, d));
        showToast('Reply posted');
    });

    const article = el('article', { class: 'post' },
        el('div', { class: 'post-head' },
            voteWidget(d, true),
            el('div', { class: 'post-titles' },
                el('h1', null, d.title),
                tagsLine(d),
                el('div', { class: 'meta' },
                    link('r/' + d.subreddit, '/r/' + d.subreddit), ' · ',
                    link('u/' + d.author, '/user/' + d.author), ' · ',
                    el('span', { title: fullDate(d.created_utc) }, age(d.created_utc)),
                    d.is_self ? null : [' · ', d.domain],
                    source !== d ? [' · crossposted from ', link('r/' + source.subreddit, '/r/' + source.subreddit)] : null))),
        mediaFor(source, d),
        body,
        el('div', { class: 'actions' },
            action('💬 Reply', () => box.open()),
            saveButton(d),
            hasDownloadableMedia(d) && action('⬇ Download', () => downloadPost(), 'Save images or video (d)'),
            action('↗ Open on Reddit', () => send({ type: 'external', url: permalink })),
            d.is_self ? null : action('🔗 Open link', () => send({ type: 'external', url: source.url || d.url })),
            action('📋 Copy link', () => navigator.clipboard.writeText(permalink).then(() => showToast('Link copied'))),
            mine && d.is_self && action('✏ Edit', () => openEditor(body, d.selftext || '',
                text => call('edit', { id: d.name, text }),
                data => (body = mdNode(data.selftext_html || '', 'post-body')))),
            mine && action('🗑 Delete', async () => {
                if (await deleteThing(d.name, 'post')) article.querySelector('h1').textContent = '[deleted] ' + d.title;
            })),
        box);

    root.append(article, commentsSection(d));
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
                el('button', { class: 'action', title: 'Find in thread (Ctrl+F)', onclick: () => openFind() }, '🔍 Find'),
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
    renderPost(post);

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
    if (!list.children.length) list.append(el('p', { class: 'muted' }, 'No comments yet.'));

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
    const line = el('div', { class: 'thread-line', title: 'Collapse thread' });
    const toggle = el('button', { class: 'collapse', title: 'Collapse' }, '−');
    const hiddenCount = el('span', { class: 'muted' });
    const authorClass = 'plain author' + (c.is_submitter ? ' op' : '') + (c.distinguished ? ' ' + c.distinguished : '');

    const head = el('div', { class: 'c-head' },
        toggle,
        c.author === '[deleted]' ? el('span', { class: 'muted' }, '[deleted]') : link(c.author, '/user/' + c.author, authorClass),
        c.is_submitter && el('span', { class: 'badge op' }, 'OP'),
        c.distinguished === 'moderator' && el('span', { class: 'badge mod' }, 'MOD'),
        c.distinguished === 'admin' && el('span', { class: 'badge admin' }, 'ADMIN'),
        c.stickied && el('span', { class: 'badge pin' }, 'PINNED'),
        isNew && el('span', { class: 'badge new' }, 'NEW'),
        el('span', { class: 'badge muted-badge' }, 'MUTED'),
        c.author_flair_text && el('span', { class: 'flair' }, c.author_flair_text),
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
    let body = mdNode(c.body_html, 'c-body');

    const actions = el('div', { class: 'c-actions' },
        voteWidget(c, false),
        action('Reply', () => box.open()),
        saveButton(c),
        mine && action('Edit', () => openEditor(body, c.body || '',
            text => call('edit', { id: c.name, text }),
            data => {
                c.body = data.body;
                return (body = mdNode(data.body_html || '', 'c-body'));
            })),
        mine && action('Delete', async () => {
            if (!(await deleteThing(c.name, 'comment'))) return;
            const gone = mdNode('<p>[deleted]</p>', 'c-body');
            body.replaceWith(gone);
            body = gone;
        }),
        action('Permalink', () => send({ type: 'external', url: 'https://www.reddit.com' + c.permalink })),
        c.author !== '[deleted]' &&
            el('button', { class: 'action menu-trigger', title: 'More', onclick: e => openUserMenu(e.currentTarget, c.author) }, '⋯'));

    if (c.replies && c.replies.data) renderChildren(c.replies.data.children, replies, depth + 1, post);

    node.append(line, el('div', { class: 'c-content' }, head, body, actions, box, replies));

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
            muted ? `Unmute u/${name}` : `🔇 Mute u/${name}`));
    document.body.append(menu);
    const r = anchor.getBoundingClientRect();
    const w = menu.getBoundingClientRect();
    menu.style.left = Math.min(r.left, window.innerWidth - w.width - 8) + 'px';
    menu.style.top = (r.bottom + w.height + 8 > window.innerHeight ? r.top - w.height - 4 : r.bottom + 4) + 'px';
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

// ---- New since last visit -------------------------------------------------------------------

function expandAncestors(node) {
    for (let c = node && node.closest('.comment'); c; c = c.parentElement && c.parentElement.closest('.comment')) {
        if (c.classList.contains('collapsed') && c.toggleCollapse) c.toggleCollapse();
    }
}

function nextNewComment() {
    const fresh = [...root.querySelectorAll('.comment.new')];
    if (!fresh.length) {
        showToast('No new comments since your last visit');
        return;
    }
    const next = fresh.find(n => n.getBoundingClientRect().top > 60) || fresh[0];
    expandAncestors(next);
    next.scrollIntoView({ block: 'center', behavior: 'smooth' });
    next.classList.remove('flash');
    void next.offsetWidth;
    next.classList.add('flash');
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
    if (window.CSS && CSS.highlights) {
        CSS.highlights.delete('find');
        CSS.highlights.delete('find-current');
    }
}

function runFind() {
    const q = findInput.value.trim().toLowerCase();
    findRanges = [];
    if (q) {
        for (const scope of root.querySelectorAll('h1, .md')) {
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
        mdNode(d.body_html, 'post-body'),
        el('div', { class: 'actions' }, action('💬 Reply', () => box.open())),
        box));
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
                el('div', { class: 'muted' }, `Redditor for ${age(info.created)}`))),
        el('div', { class: 'hc-stats' },
            el('div', null, el('b', null, count(info.karma)), el('span', { class: 'muted' }, ' karma')),
            el('div', null, el('b', null, count(info.linkKarma)), el('span', { class: 'muted' }, ' post')),
            el('div', null, el('b', null, count(info.commentKarma)), el('span', { class: 'muted' }, ' comment'))));
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

const lightbox = document.getElementById('lightbox');
const lightboxImg = lightbox.querySelector('img');
const lightboxCounter = lightbox.querySelector('.lb-counter');
let lightboxList = [];
let lightboxIndex = 0;

function openLightbox(list, index) {
    lightboxList = list;
    lightboxIndex = index;
    lightbox.classList.remove('zoomed');
    lightbox.hidden = false;
    paintLightbox();
}

function paintLightbox() {
    lightboxImg.src = lightboxList[lightboxIndex];
    const many = lightboxList.length > 1;
    lightboxCounter.textContent = many ? `${lightboxIndex + 1} / ${lightboxList.length}` : '';
    lightbox.querySelector('.lb-prev').hidden = !many;
    lightbox.querySelector('.lb-next').hidden = !many;
}

function stepLightbox(delta) {
    lightboxIndex = (lightboxIndex + delta + lightboxList.length) % lightboxList.length;
    lightbox.classList.remove('zoomed');
    paintLightbox();
}

function closeLightbox() {
    lightbox.hidden = true;
    lightboxImg.removeAttribute('src');
}

lightbox.addEventListener('click', e => {
    if (e.target === lightboxImg) lightbox.classList.toggle('zoomed');
    else if (e.target.closest('.lb-prev')) stepLightbox(-1);
    else if (e.target.closest('.lb-next')) stepLightbox(1);
    else closeLightbox();
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

const NATIVE_KEYS = new Set([' ', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End']);

document.addEventListener('keydown', e => {
    if (!lightbox.hidden) {
        if (e.key === 'Escape') closeLightbox();
        else if (e.key === 'ArrowRight') stepLightbox(1);
        else if (e.key === 'ArrowLeft') stepLightbox(-1);
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
    if (e.key.length === 1 || e.key === 'F5' || (e.ctrlKey && e.key === 'Tab')) {
        send({ type: 'key', key: e.key, ctrl: e.ctrlKey, shift: e.shiftKey });
        e.preventDefault();
    }
});

send({ type: 'ready' });
