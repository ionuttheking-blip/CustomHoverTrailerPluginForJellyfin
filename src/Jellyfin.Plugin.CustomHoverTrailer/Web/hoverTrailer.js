/**
 * Custom Hover Trailer for Jellyfin 10.11+ (v1.4)
 * - Supports 4 interaction modes:
 *   1. PC Mouse Hover (`mouse`)
 *   2. TV Remote / D-Pad Focus (`tv-focus`)
 *   3. Phone Left-to-Right Swipe on Poster Card (`mobile-swipe`) — switches immediately if another trailer is playing,
 *      and supports "Swipe Only Mode" so cards at the top/bottom edges of the phone screen can always be played!
 *   4. Phone Center-of-Screen Auto-Play (`mobile-center`)
 * - Sequential Trailer Cycling (`CycleAllAvailableTrailers`):
 *   Plays all available trailers for the movie in the configured priority order (e.g. Local -> Remote MP4 -> YouTube #1 -> YouTube #2 -> loops back to #1)
 *   instead of only repeating the first working trailer!
 * - Shows a corner Loading Source Indicator Badge (YouTube icon, Local Disk icon, Remote MP4 icon)
 *   with a spinning ring while searching/loading, and flashes a red "✕" when a candidate fails before trying the next one.
 */
(function () {
    'use strict';

    if (window.__CustomHoverTrailerInitialized) {
        return;
    }
    window.__CustomHoverTrailerInitialized = true;

    const LOG_PREFIX = '%c[CustomHoverTrailer]';
    const LOG_STYLE_INFO = 'color: #00a4dc; font-weight: bold;';
    const LOG_STYLE_WARN = 'color: #f59e0b; font-weight: bold;';
    const LOG_STYLE_ERR  = 'color: #ef4444; font-weight: bold;';

    // SVG Icons for the 3 Trailer Sources + Failure "✕"
    const SOURCE_ICONS = {
        YouTube: `<svg viewBox="0 0 24 24" width="18" height="18" fill="none" xmlns="http://www.w3.org/2000/svg">
            <path d="M22.54 6.42a2.78 2.78 0 0 0-1.94-2C18.88 4 12 4 12 4s-6.88 0-8.6.46a2.78 2.78 0 0 0-1.94 2A29 29 0 0 0 1 11.75a29 29 0 0 0 .46 5.33A2.78 2.78 0 0 0 3.4 19c1.72.46 8.6.46 8.6.46s6.88 0 8.6-.46a2.78 2.78 0 0 0 1.94-2 29 29 0 0 0 .46-5.25 29 29 0 0 0-.46-5.33z" fill="#FF0000"/>
            <polygon points="9.75 15.02 15.5 11.75 9.75 8.48 9.75 15.02" fill="#FFFFFF"/>
        </svg>`,
        LocalDisk: `<svg viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="#10b981" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round" xmlns="http://www.w3.org/2000/svg">
            <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" fill="rgba(16,185,129,0.2)"/>
            <polygon points="10 11 15 14 10 17 10 11" fill="#10b981" stroke="none"/>
        </svg>`,
        RemoteMp4: `<svg viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="#38bdf8" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" xmlns="http://www.w3.org/2000/svg">
            <circle cx="12" cy="12" r="10" fill="rgba(56,189,248,0.15)"/>
            <line x1="2" y1="12" x2="22" y2="12"/>
            <path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>
        </svg>`,
        FailedX: `<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="#ef4444" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" xmlns="http://www.w3.org/2000/svg">
            <line x1="18" y1="6" x2="6" y2="18"/>
            <line x1="6" y1="6" x2="18" y2="18"/>
        </svg>`
    };

    const DEFAULT_CONFIG = {
        EnableHoverPreviews: true,
        EnableTvFocusPreviews: true,
        EnableMobileCenterViewportPreviews: true,
        EnableMobileSwipeToPlay: true,
        MobileSwipeOnlyMode: false,
        CycleAllAvailableTrailers: true,
        HoverDelayMs: 400,
        MaxPreviewDurationSeconds: 30,
        StartMuted: true,
        ShowMuteButton: true,
        ExpandCardToWidescreenInPlace: false,
        EnableBrowserConsoleLogging: true
    };

    let config = Object.assign({}, DEFAULT_CONFIG);
    let activeSession = null;
    let triggerTimer = null;
    let mobileScrollDebounceTimer = null;
    let lastTouchTimestamp = 0;
    let suppressNextClickUntil = 0;
    let touchSwipeState = null;
    let sessionCounter = 0;
    let userHasInteractedWithPage = false;
    let sharedVideo = null;
    const infoCache = new Map();

    /**
     * Returns the single persistent <video> element reused for every trailer.
     * Keeping one element lets it keep its "allowed to play with sound" status on phones once unlocked by a tap.
     */
    function getSharedVideo() {
        if (!sharedVideo) {
            sharedVideo = document.createElement('video');
            sharedVideo.className = 'cht-video-element';
            sharedVideo.playsInline = true;
            sharedVideo.setAttribute('playsinline', '');
            sharedVideo.setAttribute('webkit-playsinline', '');
            sharedVideo.preload = 'auto';
        }
        return sharedVideo;
    }

    /**
     * True only while the browser considers a real user gesture (tap/click/key) to be active.
     * Unmuting outside of this window makes mobile browsers pause the video.
     */
    function hasLiveUserActivation() {
        if (navigator.userActivation && typeof navigator.userActivation.isActive === 'boolean') {
            return navigator.userActivation.isActive;
        }
        return false;
    }

    /**
     * Runs synchronously inside real tap/click/key handlers:
     * - If the current trailer is supposed to have sound but the browser forced it muted, unmute it now (allowed during a gesture).
     * - If nothing is playing, "prime" the shared <video> by calling play() inside the gesture, which unlocks
     *   sound for every future trailer on Android WebView / Chrome / iOS Safari.
     */
    function handleUserGestureForAudio(event) {
        userHasInteractedWithPage = true;

        // Let the mute button handle its own toggle
        if (event && event.target && event.target.closest && event.target.closest('.cht-mute-btn')) {
            return;
        }

        const isGesture = hasLiveUserActivation() || (event && (event.type === 'click' || event.type === 'keydown'));
        if (!isGesture) {
            return;
        }

        const video = getSharedVideo();
        const session = activeSession;

        if (session && session.video === video && !session.cancelled) {
            if (!session.muted && video.muted) {
                applyMuteStateToVideo(video, false);
                session.pendingUnmute = false;
                const playPromise = video.play();
                if (playPromise && playPromise.catch) {
                    playPromise.catch(function () {
                        applyMuteStateToVideo(video, true);
                        video.play().catch(function () {});
                    });
                }
                const btn = session.overlay ? session.overlay.querySelector('.cht-mute-btn') : null;
                if (btn) {
                    btn.textContent = video.muted ? '🔇' : '🔊';
                    btn.title = video.muted ? 'Unmute preview' : 'Mute preview';
                }
                logInfo('Sound enabled for the current trailer after your tap.');
            }
            return;
        }

        // Idle: prime the shared element with an in-gesture play() call (no source, the rejection is expected and harmless)
        if (!video.getAttribute('src')) {
            try {
                applyMuteStateToVideo(video, false);
                const primePromise = video.play();
                if (primePromise && primePromise.catch) {
                    primePromise.catch(function () {});
                }
            } catch (_) {
                // Ignore priming errors
            }
        }
    }

    function getConfiguredStartMuted() {
        const val = config.StartMuted ?? config.startMuted;
        return val === undefined ? true : Boolean(val);
    }

    function applyMuteStateToVideo(video, muted) {
        if (!video) {
            return;
        }
        const shouldMute = Boolean(muted);
        video.muted = shouldMute;
        video.defaultMuted = shouldMute;
        if (shouldMute) {
            video.setAttribute('muted', '');
        } else {
            video.removeAttribute('muted');
            if (video.volume === 0) {
                video.volume = 1.0;
            }
        }
    }

    function logInfo(message, ...args) {
        if (config.EnableBrowserConsoleLogging !== false && config.enableBrowserConsoleLogging !== false) {
            console.log(LOG_PREFIX + ' ' + message, LOG_STYLE_INFO, ...args);
        }
    }

    function logWarn(message, ...args) {
        console.warn(LOG_PREFIX + ' ' + message, LOG_STYLE_WARN, ...args);
    }

    function logError(message, ...args) {
        console.error(LOG_PREFIX + ' ' + message, LOG_STYLE_ERR, ...args);
    }

    function sleep(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    function isTouchPrimaryDevice() {
        if (Date.now() - lastTouchTimestamp < 15000) {
            return true;
        }
        if (window.matchMedia && window.matchMedia('(hover: none) and (pointer: coarse)').matches) {
            return true;
        }
        return ('ontouchstart' in window) && (navigator.maxTouchPoints > 0) && !window.matchMedia('(pointer: fine)').matches;
    }

    function isTvLayoutMode() {
        return document.documentElement.classList.contains('layout-tv') ||
               document.body.classList.contains('layout-tv');
    }

    function isMobileSwipeOnlyEnabled() {
        return Boolean(config.MobileSwipeOnlyMode ?? config.mobileSwipeOnlyMode ?? false);
    }

    function isMobileCenterAutoPlayEnabled() {
        if (isMobileSwipeOnlyEnabled()) {
            return false;
        }
        return (config.EnableMobileCenterViewportPreviews ?? config.enableMobileCenterViewportPreviews ?? true) !== false;
    }

    function isMobileSwipeToPlayEnabled() {
        return (config.EnableMobileSwipeToPlay ?? config.enableMobileSwipeToPlay ?? true) !== false;
    }

    function shouldCycleAllTrailers() {
        return (config.CycleAllAvailableTrailers ?? config.cycleAllAvailableTrailers ?? true) !== false;
    }

    function getBaseUrl() {
        if (window.ApiClient && typeof window.ApiClient.serverAddress === 'function') {
            return window.ApiClient.serverAddress().replace(/\/$/, '');
        }
        return '';
    }

    async function loadClientConfig() {
        try {
            const resp = await fetch(`${getBaseUrl()}/CustomHoverTrailer/ClientConfig`, { cache: 'no-store' });
            if (resp.ok) {
                const data = await resp.json();
                config = Object.assign({}, DEFAULT_CONFIG, data);
                logInfo('Loaded client configuration (StartMuted=' + getConfiguredStartMuted() + '):', config);
            }
        } catch (err) {
            logWarn('Could not fetch /CustomHoverTrailer/ClientConfig, using default config.', err);
        }
    }

    function injectStyles() {
        if (document.getElementById('custom-hover-trailer-styles')) {
            return;
        }
        const style = document.createElement('style');
        style.id = 'custom-hover-trailer-styles';
        style.textContent = `
            .cht-overlay-container {
                position: absolute;
                inset: 0;
                width: 100%;
                height: 100%;
                background: #08090c;
                z-index: 25;
                display: flex;
                align-items: center;
                justify-content: center;
                overflow: hidden;
                border-radius: inherit;
                opacity: 0;
                transition: opacity 180ms ease-in-out;
                pointer-events: none;
            }
            .cht-overlay-container.cht-visible {
                opacity: 1;
            }
            .cht-video-element {
                width: 100%;
                height: 100%;
                /* Option B: Letterboxed 16:9 inside the poster card so no video content is cropped */
                object-fit: contain;
                background: #08090c;
                pointer-events: none;
            }
            .cht-card-expanded-widescreen {
                z-index: 50 !important;
                transform: scaleX(1.35) scaleY(1.04);
                transition: transform 200ms cubic-bezier(0.2, 0, 0, 1);
                box-shadow: 0 12px 32px rgba(0, 0, 0, 0.75);
            }
            .cht-progress-bar-track {
                position: absolute;
                left: 0;
                right: 0;
                bottom: 0;
                height: 3px;
                background: rgba(255, 255, 255, 0.2);
                z-index: 27;
                pointer-events: none;
            }
            .cht-progress-bar-fill {
                height: 100%;
                width: 0%;
                background: #00a4dc;
                transition: width 120ms linear;
            }
            .cht-mute-btn {
                position: absolute;
                top: 8px;
                right: 8px;
                width: 28px;
                height: 28px;
                border-radius: 50%;
                border: 1px solid rgba(255, 255, 255, 0.25);
                background: rgba(12, 14, 18, 0.78);
                color: #fff;
                font-size: 13px;
                line-height: 1;
                display: flex;
                align-items: center;
                justify-content: center;
                cursor: pointer;
                z-index: 30;
                pointer-events: auto;
                padding: 0;
                transition: background 150ms ease, transform 120ms ease;
            }
            .cht-mute-btn:hover {
                background: rgba(0, 164, 220, 0.9);
                transform: scale(1.08);
            }
            /* Corner Loading Source Badge (Visible ONLY while searching/loading, hidden once playing) */
            .cht-status-badge {
                position: absolute;
                top: 8px;
                left: 8px;
                width: 32px;
                height: 32px;
                border-radius: 50%;
                background: rgba(10, 12, 16, 0.88);
                border: 1.5px solid rgba(255, 255, 255, 0.22);
                box-shadow: 0 4px 12px rgba(0, 0, 0, 0.65);
                display: flex;
                align-items: center;
                justify-content: center;
                z-index: 35;
                pointer-events: none;
                transition: opacity 150ms ease, transform 150ms ease, border-color 150ms ease;
            }
            .cht-status-badge::after {
                content: '';
                position: absolute;
                inset: -3px;
                border-radius: 50%;
                border: 2px solid transparent;
                border-top-color: #00a4dc;
                border-right-color: rgba(0, 164, 220, 0.45);
                animation: cht-spin 0.75s linear infinite;
            }
            .cht-status-badge.cht-badge-youtube::after {
                border-top-color: #ff0000;
                border-right-color: rgba(255, 0, 0, 0.45);
            }
            .cht-status-badge.cht-badge-local::after {
                border-top-color: #10b981;
                border-right-color: rgba(16, 185, 129, 0.45);
            }
            .cht-status-badge.cht-badge-remotemp4::after {
                border-top-color: #38bdf8;
                border-right-color: rgba(56, 189, 248, 0.45);
            }
            .cht-status-badge.cht-badge-failed {
                border-color: #ef4444;
                background: rgba(28, 8, 10, 0.92);
                transform: scale(1.08);
            }
            .cht-status-badge.cht-badge-failed::after {
                display: none;
            }
            @keyframes cht-spin {
                to { transform: rotate(360deg); }
            }
        `;
        document.head.appendChild(style);
    }

    function createOrUpdateStatusBadge(container, state, sourceType) {
        let badge = container.querySelector('.cht-status-badge');
        if (!badge) {
            badge = document.createElement('div');
            badge.className = 'cht-status-badge';
            container.appendChild(badge);
        }

        badge.classList.remove('cht-badge-youtube', 'cht-badge-local', 'cht-badge-remotemp4', 'cht-badge-failed');

        if (state === 'failed') {
            badge.classList.add('cht-badge-failed');
            badge.innerHTML = SOURCE_ICONS.FailedX;
            return badge;
        }

        if (sourceType === 'YouTube') {
            badge.classList.add('cht-badge-youtube');
            badge.innerHTML = SOURCE_ICONS.YouTube;
        } else if (sourceType === 'LocalDisk') {
            badge.classList.add('cht-badge-local');
            badge.innerHTML = SOURCE_ICONS.LocalDisk;
        } else {
            badge.classList.add('cht-badge-remotemp4');
            badge.innerHTML = SOURCE_ICONS.RemoteMp4;
        }

        return badge;
    }

    function removeStatusBadge(container) {
        if (!container) {
            return;
        }
        const badges = container.querySelectorAll('.cht-status-badge');
        badges.forEach(b => {
            if (b.parentNode) {
                b.parentNode.removeChild(b);
            }
        });
    }

    function extractItemIdFromCard(card) {
        if (!card) {
            return null;
        }
        const directId = card.getAttribute('data-id') || card.getAttribute('data-itemid');
        if (directId) {
            return directId;
        }
        const childWithId = card.querySelector('[data-id], [data-itemid]');
        if (childWithId) {
            return childWithId.getAttribute('data-id') || childWithId.getAttribute('data-itemid');
        }
        const link = card.querySelector('a[href*="id="]');
        if (link) {
            const match = link.getAttribute('href').match(/[?&]id=([a-fA-F0-9-]{32,36})/);
            if (match) {
                return match[1];
            }
        }
        return null;
    }

    function findCardImageContainer(card) {
        return (
            card.querySelector('.cardScalable') ||
            card.querySelector('.cardImageContainer') ||
            card.querySelector('.cardBox') ||
            card
        );
    }

    function isCardVisibleInViewport(card) {
        if (!card || !card.isConnected) {
            return false;
        }
        const rect = card.getBoundingClientRect();
        return rect.width > 20 &&
               rect.height > 20 &&
               rect.bottom > 0 &&
               rect.top < (window.innerHeight || document.documentElement.clientHeight) &&
               rect.right > 0 &&
               rect.left < (window.innerWidth || document.documentElement.clientWidth);
    }

    function isCardStillActiveForTrigger(card, triggerMode) {
        if (!card || !card.isConnected) {
            return false;
        }
        if (triggerMode === 'mouse') {
            return card.matches(':hover');
        }
        if (triggerMode === 'tv-focus') {
            return card.contains(document.activeElement) ||
                   card.matches(':focus-within') ||
                   card.classList.contains('card-focus') ||
                   card.classList.contains('focused') ||
                   Boolean(card.querySelector('.card-focus, :focus'));
        }
        if (triggerMode === 'mobile-center' || triggerMode === 'mobile-swipe') {
            return isCardVisibleInViewport(card);
        }
        return true;
    }

    function stopActivePreview() {
        if (triggerTimer) {
            clearTimeout(triggerTimer);
            triggerTimer = null;
        }
        if (!activeSession) {
            return;
        }

        const { card, container, video, overlay, itemId, triggerMode, listenerAbort } = activeSession;
        activeSession.cancelled = true;
        activeSession = null;

        logInfo(`Stopping [${triggerMode}] preview for item ${itemId}.`);

        if (listenerAbort) {
            listenerAbort.abort();
        }

        removeStatusBadge(container);

        try {
            if (video) {
                video.pause();
                video.removeAttribute('src');
                video.load();
            }
        } catch (_) {
            // Ignore cleanup errors
        }

        if (card) {
            card.classList.remove('cht-card-expanded-widescreen');
        }

        if (overlay && overlay.parentNode) {
            overlay.parentNode.removeChild(overlay);
        }
    }

    async function fetchTrailerInfo(itemId) {
        if (infoCache.has(itemId)) {
            return infoCache.get(itemId);
        }
        const url = `${getBaseUrl()}/CustomHoverTrailer/${encodeURIComponent(itemId)}/Info`;
        logInfo(`Fetching candidate list from: ${url}`);
        const response = await fetch(url);
        if (!response.ok) {
            const text = await response.text().catch(() => '');
            logError(`Backend returned HTTP ${response.status} for item ${itemId}:`, text);
            return { Found: false, ErrorMessage: `HTTP ${response.status}: ${text}`, CandidateSteps: [] };
        }
        const data = await response.json();
        infoCache.set(itemId, data);
        return data;
    }

    async function resolveSingleYouTubeCandidateOnServer(itemId, youtubeUrl) {
        const endpoint = `${getBaseUrl()}/CustomHoverTrailer/${encodeURIComponent(itemId)}/ResolveYouTube?url=${encodeURIComponent(youtubeUrl)}`;
        const resp = await fetch(endpoint);
        if (!resp.ok) {
            return { Success: false, ErrorMessage: `HTTP ${resp.status}` };
        }
        return await resp.json();
    }

    function toAbsoluteUrl(rawUrl) {
        if (!rawUrl) {
            return null;
        }
        return rawUrl.startsWith('http') ? rawUrl : `${getBaseUrl()}${rawUrl}`;
    }

    /**
     * Plays a single candidate stream for one cycle (up to `MaxPreviewDurationSeconds` or until `ended`),
     * with automatic retries if initial loading fails.
     * Returns an object `{ played: boolean, completedCycle: boolean }`:
     * - `played: false` means the stream failed to start (so caller should flash red "✕" and move to next candidate).
     * - `played: true, completedCycle: true` means the trailer played through its duration/ended, so caller can cycle to the next trailer!
     */
    async function playSingleCandidateForOneCycle(session, streamUrl, sourceType, itemName, maxRetries, shouldAdvanceAfterDuration) {
        const { card, container, triggerMode } = session;

        for (let attempt = 1; attempt <= maxRetries; attempt++) {
            if (session.cancelled || !isCardStillActiveForTrigger(card, triggerMode)) {
                return { played: false, completedCycle: false };
            }

            // Show the source loading badge in the top-left corner while loading/buffering this trailer
            createOrUpdateStatusBadge(container, 'searching', sourceType);

            const result = await new Promise((resolve) => {
                if (session.cancelled) {
                    resolve({ played: false, completedCycle: false });
                    return;
                }

                // Reuse or create the overlay container.
                // The <video> element is ONE shared, persistent element reused for every trailer on the page.
                // Mobile browsers (Android WebView / Chrome / iOS Safari) grant "play with sound" permission per media element
                // once it has been played inside a real user tap. Creating a fresh <video> per trailer lost that permission,
                // which is why sound was random on phones.
                let overlay = session.overlay;
                let video = getSharedVideo();
                let progressFill = overlay ? overlay.querySelector('.cht-progress-bar-fill') : null;

                // Detach listeners from the previous attempt/trailer on the shared element
                if (session.listenerAbort) {
                    session.listenerAbort.abort();
                }
                const listenerAbort = new AbortController();
                session.listenerAbort = listenerAbort;
                const on = (type, fn) => video.addEventListener(type, fn, { signal: listenerAbort.signal });

                if (!overlay || !overlay.parentNode) {
                    overlay = document.createElement('div');
                    overlay.className = 'cht-overlay-container';

                    const progressTrack = document.createElement('div');
                    progressTrack.className = 'cht-progress-bar-track';
                    progressFill = document.createElement('div');
                    progressFill.className = 'cht-progress-bar-fill';
                    progressTrack.appendChild(progressFill);

                    overlay.appendChild(video);
                    overlay.appendChild(progressTrack);

                    const showMuteBtn = (config.ShowMuteButton ?? config.showMuteButton ?? true);
                    if (showMuteBtn) {
                        const muteBtn = document.createElement('button');
                        muteBtn.type = 'button';
                        muteBtn.className = 'cht-mute-btn';
                        muteBtn.tabIndex = -1;
                        muteBtn.title = session.muted ? 'Unmute preview' : 'Mute preview';
                        muteBtn.textContent = session.muted ? '🔇' : '🔊';

                        muteBtn.addEventListener('click', function (ev) {
                            ev.preventDefault();
                            ev.stopPropagation();
                            if (session.video) {
                                session.muted = !session.video.muted;
                                applyMuteStateToVideo(session.video, session.muted);
                                muteBtn.textContent = session.muted ? '🔇' : '🔊';
                                muteBtn.title = session.muted ? 'Unmute preview' : 'Mute preview';
                            }
                        });

                        overlay.appendChild(muteBtn);
                    }

                    container.appendChild(overlay);
                    session.overlay = overlay;
                } else {
                    // Next trailer in the cycle / retry: reuse the same shared element, just stop the previous stream
                    try {
                        video.pause();
                    } catch (_) {}
                }

                if (video.parentNode !== overlay) {
                    overlay.insertBefore(video, overlay.firstChild);
                }
                session.video = video;

                if (progressFill) {
                    progressFill.style.width = '0%';
                }

                video.src = streamUrl;
                video.volume = 1.0;
                applyMuteStateToVideo(video, session.muted);
                const existingBtn = overlay.querySelector('.cht-mute-btn');
                if (existingBtn) {
                    existingBtn.textContent = session.muted ? '🔇' : '🔊';
                    existingBtn.title = session.muted ? 'Unmute preview' : 'Mute preview';
                }

                const maxDuration = Math.max(5, Number(config.MaxPreviewDurationSeconds ?? config.maxPreviewDurationSeconds) || 30);
                let hasStartedPlaying = false;
                let forcedMutedByAutoplayPolicy = false;
                let settled = false;

                // 12s startup timeout for initial buffering/muxing
                const startupTimeout = setTimeout(function () {
                    if (!hasStartedPlaying && !settled) {
                        settled = true;
                        logWarn(`Attempt ${attempt}/${maxRetries} timed out waiting for playback on "${itemName}".`);
                        resolve({ played: false, completedCycle: false });
                    }
                }, 12000);

                function finish(played, completedCycle) {
                    if (settled) {
                        return;
                    }
                    settled = true;
                    clearTimeout(startupTimeout);
                    resolve({ played, completedCycle });
                }

                // If this attempt is torn down (card left / next trailer / stop), release the awaiting caller
                listenerAbort.signal.addEventListener('abort', function () {
                    finish(hasStartedPlaying, false);
                });

                on('playing', function () {
                    hasStartedPlaying = true;
                    clearTimeout(startupTimeout);

                    // Never unmute here unless the browser confirms a live user gesture — unmuting without one
                    // makes mobile browsers pause the video on its first frame. If sound was blocked, it is
                    // restored automatically on the user's next tap (see handleUserGestureForAudio).
                    if (session.muted && !video.muted) {
                        applyMuteStateToVideo(video, true);
                    } else if (!session.muted && video.muted) {
                        if (hasLiveUserActivation()) {
                            applyMuteStateToVideo(video, false);
                        } else {
                            session.pendingUnmute = true;
                            logWarn(`Browser blocked sound for "${itemName}" (no recent tap). Sound will turn on at your next tap on the page.`);
                        }
                    }

                    const btn = overlay.querySelector('.cht-mute-btn');
                    if (btn) {
                        btn.textContent = video.muted ? '🔇' : '🔊';
                        btn.title = video.muted ? 'Unmute preview' : 'Mute preview';
                    }

                    logInfo(`Video playing (${triggerMode}, source=${sourceType}, muted=${video.muted}, attempt ${attempt}) for "${itemName}" -> ${streamUrl}`);
                    removeStatusBadge(container);
                    overlay.classList.add('cht-visible');
                    if (config.ExpandCardToWidescreenInPlace || config.expandCardToWidescreenInPlace) {
                        card.classList.add('cht-card-expanded-widescreen');
                    }
                });

                // If the mobile browser ever auto-pauses the video mid-preview (e.g. during a scroll or audio state change)
                // while the session is still active, resume it automatically so it never gets stuck on a frozen frame!
                on('pause', function () {
                    if (!settled && hasStartedPlaying && !session.cancelled && !video.ended && isCardStillActiveForTrigger(card, triggerMode)) {
                        setTimeout(function () {
                            if (!settled && !session.cancelled && video.paused && isCardStillActiveForTrigger(card, triggerMode)) {
                                video.play().catch(function () {
                                    // If resuming unmuted failed, resume muted so video keeps moving
                                    applyMuteStateToVideo(video, true);
                                    const btn = overlay.querySelector('.cht-mute-btn');
                                    if (btn) {
                                        btn.textContent = '🔇';
                                    }
                                    video.play().catch(function () {});
                                });
                            }
                        }, 80);
                    }
                });

                on('timeupdate', function () {
                    if (session.cancelled || !isCardStillActiveForTrigger(card, triggerMode)) {
                        finish(hasStartedPlaying, false);
                        return;
                    }

                    const effectiveDuration = Math.min(video.duration || maxDuration, maxDuration);
                    if (effectiveDuration > 0 && progressFill) {
                        const pct = Math.min(100, (video.currentTime / effectiveDuration) * 100);
                        progressFill.style.width = `${pct.toFixed(1)}%`;
                    }

                    if (video.currentTime >= maxDuration) {
                        if (shouldAdvanceAfterDuration) {
                            logInfo(`Finished ${maxDuration}s cycle for [${sourceType}] on "${itemName}"; advancing to next trailer in order.`);
                            finish(true, true);
                        } else {
                            video.currentTime = 0;
                            video.play().catch(function () {});
                        }
                    }
                });

                on('ended', function () {
                    if (shouldAdvanceAfterDuration) {
                        logInfo(`Trailer [${sourceType}] ended on "${itemName}"; advancing to next trailer in order.`);
                        finish(true, true);
                    } else {
                        video.currentTime = 0;
                        video.play().catch(function () {});
                    }
                });

                on('error', function () {
                    const mediaErr = video.error ? `Code ${video.error.code} (${video.error.message || 'MediaError'})` : 'Unknown media error';
                    logWarn(`Stream error on attempt ${attempt}/${maxRetries} for "${itemName}" (${streamUrl}): ${mediaErr}`);
                    finish(hasStartedPlaying, hasStartedPlaying);
                });

                video.play().catch(function (err) {
                    if (session.cancelled) {
                        finish(false, false);
                        return;
                    }
                    if (!video.muted) {
                        forcedMutedByAutoplayPolicy = true;
                        session.pendingUnmute = !session.muted;
                        logWarn(`Unmuted autoplay blocked by browser for "${itemName}", starting muted; sound turns on at your next tap.`);
                        applyMuteStateToVideo(video, true);
                        const btn = overlay.querySelector('.cht-mute-btn');
                        if (btn) {
                            btn.textContent = '🔇';
                        }
                        video.play().catch(function (err2) {
                            logWarn(`Muted play() also rejected on attempt ${attempt}/${maxRetries} for "${itemName}":`, err2.message || err2);
                            finish(false, false);
                        });
                    } else {
                        logWarn(`play() rejected on attempt ${attempt}/${maxRetries} for "${itemName}":`, err.message || err);
                        finish(false, false);
                    }
                });
            });

            if (result.played) {
                return result;
            }

            if (attempt < maxRetries && !session.cancelled && isCardStillActiveForTrigger(card, triggerMode)) {
                logInfo(`Retrying playback (${attempt + 1}/${maxRetries}) for "${itemName}" in 450ms...`);
                await sleep(450);
            }
        }

        return { played: false, completedCycle: false };
    }

    async function startPreviewOnCard(card, itemId, triggerMode) {
        // If user swiped the same card again while it's already playing, advance/restart or keep it
        if (activeSession && activeSession.card === card && !activeSession.cancelled && triggerMode !== 'mobile-swipe') {
            return;
        }

        // Stop any existing playing trailer immediately (switches to the newly swiped/hovered/focused card!)
        stopActivePreview();

        const container = findCardImageContainer(card);
        if (!container) {
            return;
        }

        const computedPosition = window.getComputedStyle(container).position;
        if (computedPosition === 'static') {
            container.style.position = 'relative';
        }

        const currentSession = {
            id: ++sessionCounter,
            card,
            container,
            itemId,
            triggerMode,
            muted: getConfiguredStartMuted(),
            video: null,
            overlay: null,
            cancelled: false
        };
        activeSession = currentSession;

        let info;
        try {
            info = await fetchTrailerInfo(itemId);
        } catch (err) {
            logError(`Network error fetching trailer info for item ${itemId}:`, err);
            if (activeSession === currentSession) {
                stopActivePreview();
            }
            return;
        }

        if (currentSession.cancelled || !isCardStillActiveForTrigger(card, triggerMode)) {
            if (activeSession === currentSession) {
                stopActivePreview();
            }
            return;
        }

        const itemName = info.ItemName || info.itemName || itemId;
        const libraryName = info.LibraryName || info.libraryName || 'Default';
        const priorityOrder = info.EffectivePriorityOrder || info.effectivePriorityOrder || 'LocalDisk -> RemoteMp4 -> YouTube';
        const diagnostics = info.Diagnostics || info.diagnostics || [];
        const steps = info.CandidateSteps || info.candidateSteps || [];

        if (diagnostics.length > 0 && config.EnableBrowserConsoleLogging !== false && config.enableBrowserConsoleLogging !== false) {
            console.groupCollapsed(
                `${LOG_PREFIX} [${triggerMode}] Candidate Plan for "${itemName}" [Library: ${libraryName} | Order: ${priorityOrder}] (${steps.length} candidate(s))`,
                LOG_STYLE_INFO
            );
            diagnostics.forEach((step, idx) => console.log(`${idx + 1}. ${step}`));
            console.groupEnd();
        }

        if (steps.length === 0) {
            logError(
                `No trailer candidates found for "${itemName}" (${itemId}). ${info.ErrorMessage || info.errorMessage || ''}`,
                { itemId, itemName, triggerMode }
            );
            if (activeSession === currentSession) {
                stopActivePreview();
            }
            return;
        }

        const cycleAll = shouldCycleAllTrailers() && steps.length > 1;
        const failedStepIndices = new Set();

        // Outer loop: keeps cycling through all valid candidates in the selected priority order
        // while the card remains active!
        while (!currentSession.cancelled && isCardStillActiveForTrigger(card, triggerMode)) {
            let anyPlayedInThisPass = false;

            for (let i = 0; i < steps.length; i++) {
                if (currentSession.cancelled || !isCardStillActiveForTrigger(card, triggerMode)) {
                    if (activeSession === currentSession) {
                        stopActivePreview();
                    }
                    return;
                }

                // Skip candidates that already permanently failed during this hover session
                if (failedStepIndices.has(i)) {
                    continue;
                }

                const step = steps[i];
                const sourceType = step.SourceType || step.sourceType || 'YouTube';
                const label = step.Label || step.label || '';

                // 1. Show the source searching/loading icon in the top-left corner while resolving/loading
                createOrUpdateStatusBadge(container, 'searching', sourceType);
                logInfo(`Trying candidate ${i + 1}/${steps.length} [${sourceType}] for "${itemName}": ${label}`);

                let streamUrlToPlay = step._resolvedStreamUrl || null;

                if (!streamUrlToPlay) {
                    if (sourceType === 'YouTube') {
                        const ytUrl = step.YouTubeUrl || step.youTubeUrl || label;
                        try {
                            const res = await resolveSingleYouTubeCandidateOnServer(itemId, ytUrl);
                            const success = Boolean(res.Success ?? res.success);
                            const resolvedStream = res.StreamUrl || res.streamUrl;

                            if (success && resolvedStream) {
                                streamUrlToPlay = toAbsoluteUrl(resolvedStream);
                                step._resolvedStreamUrl = streamUrlToPlay;
                            } else {
                                logWarn(`YouTube candidate ${i + 1}/${steps.length} (${ytUrl}) failed resolution: ${res.ErrorMessage || res.errorMessage}`);
                            }
                        } catch (ytErr) {
                            logWarn(`YouTube candidate ${i + 1}/${steps.length} (${ytUrl}) threw error:`, ytErr);
                        }
                    } else {
                        streamUrlToPlay = toAbsoluteUrl(step.DirectStreamUrl || step.directStreamUrl);
                        step._resolvedStreamUrl = streamUrlToPlay;
                    }
                }

                if (currentSession.cancelled || !isCardStillActiveForTrigger(card, triggerMode)) {
                    if (activeSession === currentSession) {
                        stopActivePreview();
                    }
                    return;
                }

                if (streamUrlToPlay) {
                    const maxRetries = triggerMode === 'tv-focus' ? 3 : 2;
                    // Advance after duration if CycleAllAvailableTrailers is enabled and there are other non-failed candidates
                    const hasOtherValidCandidates = cycleAll && (steps.length - failedStepIndices.size > 1);
                    const playResult = await playSingleCandidateForOneCycle(
                        currentSession,
                        streamUrlToPlay,
                        sourceType,
                        itemName,
                        maxRetries,
                        hasOtherValidCandidates
                    );

                    if (playResult.played) {
                        anyPlayedInThisPass = true;
                        if (!hasOtherValidCandidates || !playResult.completedCycle) {
                            // If not cycling (or session ended), exit
                            return;
                        }
                        // Otherwise, `completedCycle` is true -> loop continues to candidate `i + 1` in the priority list!
                        continue;
                    }
                }

                // Candidate `i` failed! Mark it as failed for this session and flash the red "✕" icon for 550ms
                failedStepIndices.add(i);
                if (!currentSession.cancelled && isCardStillActiveForTrigger(card, triggerMode)) {
                    createOrUpdateStatusBadge(container, 'failed', sourceType);
                    await sleep(550);
                }
            }

            if (!anyPlayedInThisPass) {
                logError(`All ${steps.length} trailer candidate(s) failed for "${itemName}" (${itemId}).`);
                if (activeSession === currentSession) {
                    stopActivePreview();
                }
                return;
            }
        }
    }

    // =========================================================================
    // 1. PC / Mouse Hover Handler (Disabled during touch interactions!)
    // =========================================================================
    function handleMouseOver(event) {
        if (config.EnableHoverPreviews === false || config.enableHoverPreviews === false) {
            return;
        }

        if (isTouchPrimaryDevice() || isTvLayoutMode()) {
            return;
        }

        const card = event.target && event.target.closest ? event.target.closest('.card') : null;
        if (!card) {
            return;
        }

        if (activeSession && activeSession.card === card && !activeSession.cancelled) {
            return;
        }

        const itemId = extractItemIdFromCard(card);
        if (!itemId) {
            return;
        }

        if (triggerTimer) {
            clearTimeout(triggerTimer);
        }

        const delay = Math.max(100, Number(config.HoverDelayMs ?? config.hoverDelayMs) || 400);
        triggerTimer = setTimeout(function () {
            if (card.matches(':hover') && !isTouchPrimaryDevice()) {
                startPreviewOnCard(card, itemId, 'mouse');
            }
        }, delay);

        const onMouseLeave = function () {
            card.removeEventListener('mouseleave', onMouseLeave);
            if (triggerTimer) {
                clearTimeout(triggerTimer);
                triggerTimer = null;
            }
            if (activeSession && activeSession.card === card && activeSession.triggerMode === 'mouse') {
                stopActivePreview();
            }
        };

        card.addEventListener('mouseleave', onMouseLeave);
    }

    // =========================================================================
    // 2. TV / Remote D-Pad / Keyboard Focus Handler
    // =========================================================================
    function triggerTvFocusForCard(card) {
        if (config.EnableTvFocusPreviews === false || config.enableTvFocusPreviews === false) {
            return;
        }

        if (Date.now() - lastTouchTimestamp < 1200 && !isTvLayoutMode()) {
            return;
        }

        if (!card) {
            return;
        }

        if (activeSession && activeSession.card === card && !activeSession.cancelled) {
            return;
        }

        const itemId = extractItemIdFromCard(card);
        if (!itemId) {
            return;
        }

        if (triggerTimer) {
            clearTimeout(triggerTimer);
        }

        const delay = Math.max(150, Number(config.HoverDelayMs ?? config.hoverDelayMs) || 400);
        triggerTimer = setTimeout(function () {
            if (isCardStillActiveForTrigger(card, 'tv-focus')) {
                startPreviewOnCard(card, itemId, 'tv-focus');
            }
        }, delay);
    }

    function handleFocusIn(event) {
        const card = event.target && event.target.closest ? event.target.closest('.card') : null;
        if (card) {
            triggerTvFocusForCard(card);
        }
    }

    function handleFocusOut() {
        if (!activeSession || activeSession.triggerMode !== 'tv-focus') {
            return;
        }

        const activeCard = activeSession.card;
        setTimeout(function () {
            if (activeSession && activeSession.card === activeCard && !isCardStillActiveForTrigger(activeCard, 'tv-focus')) {
                stopActivePreview();
            }
        }, 60);
    }

    // =========================================================================
    // 3. Phone Left-to-Right Swipe Gesture Handler on Movie Poster Cards
    // =========================================================================
    function handleTouchStartForSwipe(event) {
        lastTouchTimestamp = Date.now();
        userHasInteractedWithPage = true;

        if (!isMobileSwipeToPlayEnabled()) {
            touchSwipeState = null;
            return;
        }

        if (!event.touches || event.touches.length !== 1) {
            touchSwipeState = null;
            return;
        }

        // Ignore touches on the mute/unmute button itself
        if (event.target && event.target.closest && event.target.closest('.cht-mute-btn')) {
            touchSwipeState = null;
            return;
        }

        const card = event.target && event.target.closest ? event.target.closest('.card') : null;
        if (!card) {
            touchSwipeState = null;
            return;
        }

        const touch = event.touches[0];
        touchSwipeState = {
            card: card,
            startX: touch.clientX,
            startY: touch.clientY,
            lastX: touch.clientX,
            lastY: touch.clientY,
            startTime: Date.now()
        };
    }

    function handleTouchMoveForSwipe(event) {
        if (!touchSwipeState || !event.touches || event.touches.length !== 1) {
            return;
        }
        const touch = event.touches[0];
        touchSwipeState.lastX = touch.clientX;
        touchSwipeState.lastY = touch.clientY;
    }

    function handleTouchEndForSwipe(event) {
        lastTouchTimestamp = Date.now();
        userHasInteractedWithPage = true;

        if (touchSwipeState && isMobileSwipeToPlayEnabled()) {
            const state = touchSwipeState;
            touchSwipeState = null;

            const endTouch = (event.changedTouches && event.changedTouches.length > 0)
                ? event.changedTouches[0]
                : { clientX: state.lastX, clientY: state.lastY };

            const deltaX = endTouch.clientX - state.startX;
            const deltaY = endTouch.clientY - state.startY;
            const elapsedMs = Date.now() - state.startTime;

            // Left-to-Right swipe detection:
            // - Moved right by at least 35px (deltaX >= 35)
            // - Predominantly horizontal (deltaX > Math.abs(deltaY) * 1.35)
            // - Completed within 1200ms (so it's a swipe, not a long hold)
            if (deltaX >= 35 && deltaX > Math.abs(deltaY) * 1.35 && elapsedMs <= 1200) {
                const itemId = extractItemIdFromCard(state.card);
                if (itemId) {
                    // Cancel any pending center-viewport timer and suppress accidental link navigation on swipe release
                    if (mobileScrollDebounceTimer) {
                        clearTimeout(mobileScrollDebounceTimer);
                        mobileScrollDebounceTimer = null;
                    }
                    suppressNextClickUntil = Date.now() + 450;

                    logInfo(`Left-to-right swipe detected on card (${itemId}, deltaX=${Math.round(deltaX)}px). Switching trailer!`);
                    startPreviewOnCard(state.card, itemId, 'mobile-swipe');
                    return;
                }
            }
        }

        scheduleMobileCenterCheck();
    }

    // =========================================================================
    // 4. Phone / Touchscreen Viewport Center Detector
    // =========================================================================
    function findCardClosestToViewportCenter() {
        const allCards = document.querySelectorAll('.card');
        if (!allCards || allCards.length === 0) {
            return null;
        }

        const viewportWidth = window.innerWidth || document.documentElement.clientWidth;
        const viewportHeight = window.innerHeight || document.documentElement.clientHeight;
        const viewportCenterX = viewportWidth / 2;
        const viewportCenterY = viewportHeight / 2;

        const minCenterBandY = viewportHeight * 0.16;
        const maxCenterBandY = viewportHeight * 0.84;

        let bestCard = null;
        let bestDistanceScore = Infinity;

        for (let i = 0; i < allCards.length; i++) {
            const card = allCards[i];
            if (!card.offsetParent && window.getComputedStyle(card).display === 'none') {
                continue;
            }

            const rect = card.getBoundingClientRect();
            if (rect.width < 30 || rect.height < 30) {
                continue;
            }

            const visibleTop = Math.max(0, rect.top);
            const visibleBottom = Math.min(viewportHeight, rect.bottom);
            const visibleLeft = Math.max(0, rect.left);
            const visibleRight = Math.min(viewportWidth, rect.right);

            const visibleHeight = visibleBottom - visibleTop;
            const visibleWidth = visibleRight - visibleLeft;

            if (visibleHeight < rect.height * 0.55 || visibleWidth < rect.width * 0.55) {
                continue;
            }

            const cardCenterX = rect.left + (rect.width / 2);
            const cardCenterY = rect.top + (rect.height / 2);

            if (cardCenterY < minCenterBandY || cardCenterY > maxCenterBandY) {
                continue;
            }

            const dy = cardCenterY - viewportCenterY;
            const dx = cardCenterX - viewportCenterX;
            const distanceScore = Math.hypot(dx * 0.75, dy * 1.35);

            if (distanceScore < bestDistanceScore) {
                bestDistanceScore = distanceScore;
                bestCard = card;
            }
        }

        return bestCard;
    }

    function scheduleMobileCenterCheck() {
        // Stop any mobile-swipe or mobile-center trailer if the user scrolled its card completely off-screen
        if (activeSession && (activeSession.triggerMode === 'mobile-center' || activeSession.triggerMode === 'mobile-swipe')) {
            if (!isCardVisibleInViewport(activeSession.card)) {
                stopActivePreview();
            }
        }

        // If user is in "Mobile Swipe-Only Mode" (or disabled center auto-play), do not auto-trigger center cards!
        if (!isMobileCenterAutoPlayEnabled()) {
            return;
        }

        if (!isTouchPrimaryDevice() || isTvLayoutMode()) {
            return;
        }

        // If the user explicitly swiped a card and that card is still visible on screen, don't override their swiped choice with center-autoplay!
        if (activeSession && activeSession.triggerMode === 'mobile-swipe' && !activeSession.cancelled && isCardVisibleInViewport(activeSession.card)) {
            return;
        }

        if (activeSession && activeSession.triggerMode === 'mobile-center') {
            const rect = activeSession.card.getBoundingClientRect();
            const vh = window.innerHeight || document.documentElement.clientHeight;
            const cardCenterY = rect.top + (rect.height / 2);
            if (cardCenterY < vh * 0.12 || cardCenterY > vh * 0.88) {
                stopActivePreview();
            }
        }

        if (mobileScrollDebounceTimer) {
            clearTimeout(mobileScrollDebounceTimer);
        }

        const delay = Math.max(250, Number(config.HoverDelayMs ?? config.hoverDelayMs) || 450);
        mobileScrollDebounceTimer = setTimeout(function () {
            if (document.hidden || !isTouchPrimaryDevice() || !isMobileCenterAutoPlayEnabled()) {
                return;
            }

            if (activeSession && activeSession.triggerMode === 'mobile-swipe' && !activeSession.cancelled && isCardVisibleInViewport(activeSession.card)) {
                return;
            }

            const centeredCard = findCardClosestToViewportCenter();
            if (!centeredCard) {
                return;
            }

            if (activeSession && activeSession.card === centeredCard && !activeSession.cancelled) {
                return;
            }

            const itemId = extractItemIdFromCard(centeredCard);
            if (!itemId) {
                return;
            }

            startPreviewOnCard(centeredCard, itemId, 'mobile-center');
        }, delay);
    }

    async function initialize() {
        logInfo('Initializing Custom Hover Trailer v1.4 (Swipe-to-Play + Sequential Trailer Cycling + Source Badges)...');
        injectStyles();
        await loadClientConfig();

        window.addEventListener('touchstart', handleTouchStartForSwipe, { passive: true, capture: true });
        window.addEventListener('touchmove', handleTouchMoveForSwipe, { passive: true, capture: true });
        window.addEventListener('touchend', handleTouchEndForSwipe, { passive: true, capture: true });

        // Real taps / clicks / key presses unlock and restore trailer sound on phones and TVs
        ['pointerup', 'touchend', 'click', 'keydown'].forEach(function (evtName) {
            window.addEventListener(evtName, handleUserGestureForAudio, { passive: true, capture: true });
        });

        // Prevent a left-to-right swipe release on a card from accidentally clicking/opening the movie details page
        window.addEventListener('click', function (ev) {
            if (Date.now() < suppressNextClickUntil) {
                ev.preventDefault();
                ev.stopPropagation();
                suppressNextClickUntil = 0;
            }
        }, true);

        window.addEventListener('scroll', scheduleMobileCenterCheck, { passive: true, capture: true });

        // 1. PC Mouse Hover
        document.addEventListener('mouseover', handleMouseOver, { passive: true });

        // 2. TV / D-Pad / Remote Focus
        document.addEventListener('focusin', handleFocusIn, { passive: true, capture: true });
        document.addEventListener('focusout', handleFocusOut, { passive: true, capture: true });

        document.addEventListener('keyup', function (e) {
            if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.key)) {
                setTimeout(function () {
                    const focusedEl = document.activeElement || document.querySelector('.card-focus, .focused');
                    const card = focusedEl && focusedEl.closest ? focusedEl.closest('.card') : null;
                    if (card) {
                        triggerTvFocusForCard(card);
                    }
                }, 60);
            }
        }, { passive: true, capture: true });

        const tvFocusObserver = new MutationObserver(function (mutations) {
            if (config.EnableTvFocusPreviews === false || config.enableTvFocusPreviews === false) {
                return;
            }
            for (let i = 0; i < mutations.length; i++) {
                const target = mutations[i].target;
                if (target && target.nodeType === 1 && target.classList) {
                    if (target.classList.contains('card-focus') || target.classList.contains('focused')) {
                        const card = target.closest('.card') || target;
                        triggerTvFocusForCard(card);
                    }
                }
            }
        });

        tvFocusObserver.observe(document.body, {
            subtree: true,
            attributes: true,
            attributeFilter: ['class']
        });

        window.addEventListener('hashchange', function () {
            stopActivePreview();
            scheduleMobileCenterCheck();
        });
        window.addEventListener('popstate', function () {
            stopActivePreview();
            scheduleMobileCenterCheck();
        });
        document.addEventListener('visibilitychange', function () {
            if (document.hidden) {
                stopActivePreview();
            } else {
                scheduleMobileCenterCheck();
            }
        });

        setTimeout(scheduleMobileCenterCheck, 1200);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initialize);
    } else {
        initialize();
    }
})();
