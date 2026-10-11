'use strict';

// The floating player window: plays one video, picking up where the post view left off.
// What to play arrives in the address: #{"kind":"reddit"|"hls"|"mp4","url":…,"poster":…,"time":…}.
(() => {
    let source;
    try {
        source = JSON.parse(decodeURIComponent(location.hash.slice(1)));
    } catch {
        return;
    }
    const https = u => typeof u === 'string' && u.startsWith('https://');
    if (!source || !https(source.url)) return;
    const poster = https(source.poster) ? source.poster : null;
    const video = source.kind === 'reddit' ? redditVideo(source.url, poster, false)
        : source.kind === 'hls' ? videoPlayer(source.url, null, poster, false)
        : videoPlayer(null, source.url, poster, false);
    video.autoplay = true;
    if (source.time > 0) video.addEventListener('loadedmetadata', () => { video.currentTime = source.time; }, { once: true });
    document.getElementById('player').append(video);
})();
