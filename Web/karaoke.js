(function () {
    'use strict';

    console.log('[Monochrome] Apple Music Karaoke & Lyrics client initialized.');

    let currentLyrics = null;
    let currentTrackId = null;
    let currentItemId = null;
    let isModalOpen = false;
    let userHasScrolled = false;
    let scrollTimeout = null;

    // 1. Build and inject the Karaoke Modal DOM
    function ensureModalDOMElements() {
        if (document.getElementById('monochromeKaraokeModal')) {
            return;
        }

        const modal = document.createElement('div');
        modal.id = 'monochromeKaraokeModal';
        modal.innerHTML = `
            <div id="monochromeKaraokeBackdrop"></div>
            <div id="monochromeKaraokeOverlay"></div>
            <div class="monochrome-karaoke-header">
                <div class="monochrome-karaoke-meta">
                    <img id="monochromeKaraokeThumb" class="monochrome-karaoke-thumb" src="" alt="Album Art" />
                    <div class="monochrome-karaoke-titles">
                        <h2 id="monochromeKaraokeSong" class="monochrome-karaoke-song"></h2>
                        <p id="monochromeKaraokeArtist" class="monochrome-karaoke-artist"></p>
                    </div>
                </div>
                <button id="monochromeKaraokeClose" class="monochrome-karaoke-close" title="Chiudi Testi">✕</button>
            </div>
            <div id="monochromeKaraokeScroll">
                <div class="monochrome-karaoke-empty">Caricamento testi...</div>
            </div>
        `;

        document.body.appendChild(modal);

        // Close handlers
        document.getElementById('monochromeKaraokeClose').addEventListener('click', closeModal);
        modal.addEventListener('click', (e) => {
            if (e.target === modal || e.target.id === 'monochromeKaraokeOverlay') {
                closeModal();
            }
        });

        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && isModalOpen) {
                closeModal();
            }
        });

        // Detect user manual scroll to avoid jitter
        const scrollContainer = document.getElementById('monochromeKaraokeScroll');
        scrollContainer.addEventListener('wheel', () => {
            userHasScrolled = true;
            clearTimeout(scrollTimeout);
            scrollTimeout = setTimeout(() => { userHasScrolled = false; }, 3500);
        });
        scrollContainer.addEventListener('touchmove', () => {
            userHasScrolled = true;
            clearTimeout(scrollTimeout);
            scrollTimeout = setTimeout(() => { userHasScrolled = false; }, 3500);
        });
    }

    function openModal() {
        ensureModalDOMElements();
        const modal = document.getElementById('monochromeKaraokeModal');
        if (modal) {
            modal.classList.add('active');
            isModalOpen = true;
            updateModalTrackInfo();
            if (currentTrackId || currentItemId) {
                loadLyrics(currentTrackId, currentItemId);
            }
        }
    }

    function closeModal() {
        const modal = document.getElementById('monochromeKaraokeModal');
        if (modal) {
            modal.classList.remove('active');
            isModalOpen = false;
        }
    }

    // 2. Inject the prominent "🎤 Testi" button in nowPlayingBar and nowPlayingPage
    function injectLyricsButtons() {
        // Bottom Player Bar
        const barContainers = document.querySelectorAll('.nowPlayingBar, .nowPlayingInfoButtons, .nowPlayingSecondaryButtons');
        barContainers.forEach(container => {
            if (container.querySelector('#btnMonochromeLyrics')) {
                return;
            }

            const btn = document.createElement('button');
            btn.id = 'btnMonochromeLyrics';
            btn.type = 'button';
            btn.className = 'btnMonochromeLyrics paper-icon-button-light autoSize';
            btn.title = 'Testi Karaoke (Apple Music Style)';
            btn.innerHTML = '<span class="mic-icon">🎤</span> <span>Testi</span>';
            btn.addEventListener('click', (e) => {
                e.stopPropagation();
                if (isModalOpen) {
                    closeModal();
                } else {
                    openModal();
                }
            });

            // Insert near controls or secondary buttons
            if (container.classList.contains('nowPlayingSecondaryButtons')) {
                container.prepend(btn);
            } else if (container.classList.contains('nowPlayingInfoButtons')) {
                const nextBtn = container.querySelector('.btnNextTrack');
                if (nextBtn && nextBtn.nextSibling) {
                    container.insertBefore(btn, nextBtn.nextSibling);
                } else {
                    container.appendChild(btn);
                }
            } else {
                container.appendChild(btn);
            }
        });

        // Ensure next button advances radio when local queue is exhausted
        const nextButtons = document.querySelectorAll('.btnNextTrack');
        nextButtons.forEach(btn => {
            if (btn.getAttribute('data-mono-hooked')) return;
            btn.setAttribute('data-mono-hooked', 'true');
            btn.addEventListener('click', () => {
                // If the player stopped or has no next track in local queue, trigger backend radio skip
                setTimeout(() => {
                    const audioElem = document.querySelector('audio');
                    if (!audioElem || audioElem.paused || audioElem.ended) {
                        triggerBackendNextTrack();
                    }
                }, 400);
            });
        });
    }

    async function triggerBackendNextTrack() {
        try {
            console.log('[Monochrome] Requesting next radio track from backend...');
            await fetch('/Monochrome/Playback/Next', { credentials: 'omit' });
        } catch (e) {
            console.warn('[Monochrome] Could not trigger backend next track:', e);
        }
    }

    // 3. Update Modal Header & Background with current track details
    function updateModalTrackInfo() {
        const titleElem = document.querySelector('.nowPlayingSongName, .nowPlayingPageTitle, .nowPlayingBarTextTitle');
        const artistElem = document.querySelector('.nowPlayingArtist, .nowPlayingBarTextArtist');
        const imageElem = document.querySelector('.nowPlayingPageImageContainer img, .nowPlayingBarImage img, .nowPlayingImage img');

        const songTitle = titleElem ? titleElem.textContent.trim() : 'Musica';
        const artistName = artistElem ? artistElem.textContent.trim() : '';
        const coverSrc = imageElem ? imageElem.src : '';

        const modalSong = document.getElementById('monochromeKaraokeSong');
        const modalArtist = document.getElementById('monochromeKaraokeArtist');
        const modalThumb = document.getElementById('monochromeKaraokeThumb');
        const modalBackdrop = document.getElementById('monochromeKaraokeBackdrop');

        if (modalSong) modalSong.textContent = songTitle;
        if (modalArtist) modalArtist.textContent = artistName;
        if (modalThumb) {
            modalThumb.src = coverSrc || '';
            modalThumb.style.display = coverSrc ? 'block' : 'none';
        }
        if (modalBackdrop && coverSrc) {
            modalBackdrop.style.backgroundImage = `url("${coverSrc}")`;
        }
    }

    // 4. Fetch and render lyrics
    async function loadLyrics(trackId, itemId) {
        const scrollContainer = document.getElementById('monochromeKaraokeScroll');
        if (!scrollContainer) return;

        scrollContainer.innerHTML = '<div class="monochrome-karaoke-empty">Ricerca testi sincronizzati...</div>';

        let data = null;

        // Try direct controller endpoint
        const endpoints = [];
        if (trackId) endpoints.push(`/Monochrome/Tracks/${trackId}/Lyrics`);
        if (itemId) endpoints.push(`/Monochrome/Tracks/ByGuid/${itemId}/Lyrics`);
        if (itemId) endpoints.push(`/Audio/${itemId}/Lyrics`);

        for (const url of endpoints) {
            try {
                const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
                if (res.ok) {
                    data = await res.json();
                    if (data && (data.lines?.length || data.Lyrics?.length || data.plainLyrics)) {
                        break;
                    }
                }
            } catch (e) {
                // Try next endpoint
            }
        }

        if (!data) {
            scrollContainer.innerHTML = '<div class="monochrome-karaoke-empty">Nessun testo disponibile per questo brano.</div>';
            currentLyrics = null;
            return;
        }

        // Normalize lyrics list
        let lines = [];
        if (data.lines && Array.isArray(data.lines)) {
            lines = data.lines;
        } else if (data.Lyrics && Array.isArray(data.Lyrics)) {
            lines = data.Lyrics.map(l => ({
                time: (l.Start || 0) / 10000000.0,
                text: l.Text || ''
            }));
        } else if (data.plainLyrics) {
            const rawLines = data.plainLyrics.split('\n');
            lines = rawLines.map((t, idx) => ({ time: idx * 5, text: t.trim() })).filter(l => l.text);
        }

        currentLyrics = lines;

        if (lines.length === 0) {
            scrollContainer.innerHTML = '<div class="monochrome-karaoke-empty">Nessun testo disponibile per questo brano.</div>';
            return;
        }

        // Render line elements
        scrollContainer.innerHTML = '';
        lines.forEach((l, idx) => {
            const div = document.createElement('div');
            div.className = 'monochrome-lyric-line';
            div.id = `monoLyric_${idx}`;
            div.setAttribute('data-time', l.time);
            div.textContent = l.text;

            // Click-to-seek
            div.addEventListener('click', () => {
                seekAudioTo(l.time);
            });

            scrollContainer.appendChild(div);
        });
    }

    function seekAudioTo(seconds) {
        const audio = document.querySelector('audio');
        if (audio && !isNaN(seconds)) {
            audio.currentTime = seconds;
            if (audio.paused) {
                audio.play().catch(() => {});
            }
        }
    }

    // 5. Real-time Karaoke Time Sync Loop
    let lastActiveIdx = -1;

    function syncLyricsLoop() {
        requestAnimationFrame(syncLyricsLoop);

        if (!isModalOpen || !currentLyrics || currentLyrics.length === 0) {
            return;
        }

        const audio = document.querySelector('audio');
        if (!audio) return;

        const currentTime = audio.currentTime || 0;

        // Find current active line
        let activeIdx = -1;
        for (let i = currentLyrics.length - 1; i >= 0; i--) {
            if (currentLyrics[i].time <= currentTime + 0.15) {
                activeIdx = i;
                break;
            }
        }

        if (activeIdx === lastActiveIdx) {
            return;
        }

        lastActiveIdx = activeIdx;

        const scrollContainer = document.getElementById('monochromeKaraokeScroll');
        if (!scrollContainer) return;

        // Update classes
        currentLyrics.forEach((_, idx) => {
            const el = document.getElementById(`monoLyric_${idx}`);
            if (!el) return;

            if (idx === activeIdx) {
                el.classList.add('active');
                el.classList.remove('passed');
            } else if (idx < activeIdx) {
                el.classList.remove('active');
                el.classList.add('passed');
            } else {
                el.classList.remove('active');
                el.classList.remove('passed');
            }
        });

        // Smooth scroll to active line
        if (activeIdx >= 0 && !userHasScrolled) {
            const activeEl = document.getElementById(`monoLyric_${activeIdx}`);
            if (activeEl) {
                const targetScroll = activeEl.offsetTop - (scrollContainer.clientHeight * 0.4);
                scrollContainer.scrollTo({
                    top: Math.max(0, targetScroll),
                    behavior: 'smooth'
                });
            }
        }
    }

    // 6. Hook into playback changes to refresh track metadata
    function monitorPlaybackChanges() {
        const audio = document.querySelector('audio');
        if (audio) {
            audio.addEventListener('play', () => {
                updateModalTrackInfo();
                checkTrackChange();
            });
            audio.addEventListener('loadedmetadata', () => {
                updateModalTrackInfo();
                checkTrackChange();
            });
        }

        // Periodic DOM check
        setInterval(() => {
            injectLyricsButtons();
            checkTrackChange();
        }, 1200);
    }

    function checkTrackChange() {
        const audio = document.querySelector('audio');
        const src = audio ? (audio.src || '') : '';
        const match = src.match(/\/Audio\/([a-zA-Z0-9_-]+)/i);
        const newId = match ? match[1] : null;

        if (newId && newId !== currentItemId) {
            currentItemId = newId;
            currentTrackId = null;
            updateModalTrackInfo();
            if (isModalOpen) {
                loadLyrics(null, currentItemId);
            }
        }
    }

    // Initialize
    ensureModalDOMElements();
    injectLyricsButtons();
    monitorPlaybackChanges();
    syncLyricsLoop();

})();
