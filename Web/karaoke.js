(function () {
    'use strict';

    console.log('[Monochrome] Apple Music Karaoke & Lyrics client v1.3.9.2 loaded.');

    let currentLyrics = null;
    let currentLoadedKey = null;
    let isModalOpen = false;
    let userHasScrolled = false;
    let scrollTimeout = null;
    let lastActiveIdx = -1;

    // 1. DOM modal initialization
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
                <div class="monochrome-karaoke-empty">Caricamento testi in corso...</div>
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

        const scrollContainer = document.getElementById('monochromeKaraokeScroll');
        const setScrolled = () => {
            userHasScrolled = true;
            clearTimeout(scrollTimeout);
            scrollTimeout = setTimeout(() => { userHasScrolled = false; }, 3500);
        };
        scrollContainer.addEventListener('wheel', setScrolled);
        scrollContainer.addEventListener('touchmove', setScrolled);
    }

    // 2. Extract current track details from multiple sources
    function getCurrentTrackDetails() {
        let title = '';
        let artist = '';
        let itemId = '';
        let cover = '';

        // A. From global playbackManager if accessible
        try {
            if (window.playbackManager && typeof window.playbackManager.getCurrentPlayer === 'function') {
                const player = window.playbackManager.getCurrentPlayer();
                const item = window.playbackManager.currentItem(player);
                if (item) {
                    title = item.Name || '';
                    artist = (item.Artists && item.Artists.length > 0) ? item.Artists[0] : (item.AlbumArtist || '');
                    itemId = item.Id || '';
                    if (item.ImageTags && item.ImageTags.Primary) {
                        cover = `/Items/${item.Id}/Images/Primary?maxWidth=600&quality=90`;
                    }
                }
            }
        } catch (e) { }

        // B. From DOM elements if title/artist not yet found
        if (!title) {
            const titleElem = document.querySelector('.nowPlayingSongName, .nowPlayingPageTitle, .nowPlayingBarTextTitle, .nowPlayingBarText .title');
            if (titleElem) title = titleElem.textContent.trim();
        }

        if (!artist) {
            const artistElem = document.querySelector('.nowPlayingArtist, .nowPlayingBarTextArtist, .nowPlayingBarText .artist');
            if (artistElem) artist = artistElem.textContent.trim();
        }

        if (!itemId) {
            const ratingBtn = document.querySelector('.nowPlayingBar [data-id], .nowPlayingPage [data-id]');
            if (ratingBtn) itemId = ratingBtn.getAttribute('data-id') || '';
        }

        if (!itemId) {
            const audio = document.querySelector('audio');
            const src = audio ? (audio.src || '') : '';
            const match = src.match(/\/(?:Audio|Items)\/([a-zA-Z0-9_-]{32})/i);
            if (match) itemId = match[1];
        }

        if (!cover) {
            const imgElem = document.querySelector('.nowPlayingPageImageContainer img, .nowPlayingBarImage img, .nowPlayingImage img, .nowPlayingPageImagePoster');
            if (imgElem) cover = imgElem.src || '';
        }

        return { title, artist, itemId, cover };
    }

    function openModal() {
        ensureModalDOMElements();
        const modal = document.getElementById('monochromeKaraokeModal');
        if (modal) {
            modal.classList.add('active');
            isModalOpen = true;
            updateModalHeader();
            loadLyrics();
        }
    }

    function closeModal() {
        const modal = document.getElementById('monochromeKaraokeModal');
        if (modal) {
            modal.classList.remove('active');
            isModalOpen = false;
        }
    }

    function toggleModal() {
        if (isModalOpen) closeModal();
        else openModal();
    }

    function updateModalHeader() {
        const info = getCurrentTrackDetails();
        const modalSong = document.getElementById('monochromeKaraokeSong');
        const modalArtist = document.getElementById('monochromeKaraokeArtist');
        const modalThumb = document.getElementById('monochromeKaraokeThumb');
        const modalBackdrop = document.getElementById('monochromeKaraokeBackdrop');

        if (modalSong) modalSong.textContent = info.title || 'Musica';
        if (modalArtist) modalArtist.textContent = info.artist || '';
        if (modalThumb) {
            modalThumb.src = info.cover || '';
            modalThumb.style.display = info.cover ? 'block' : 'none';
        }
        if (modalBackdrop && info.cover) {
            modalBackdrop.style.backgroundImage = `url("${info.cover}")`;
        }
    }

    // 3. Robust button injection in all player bar zones
    function injectLyricsButtons() {
        const isAudioActive = !!document.querySelector('audio') || !document.querySelector('.nowPlayingBar-hidden');

        // A. Dedicated button in .nowPlayingBarCenter (beside next button)
        const center = document.querySelector('.nowPlayingBarCenter');
        if (center && !center.querySelector('.btnMonochromeLyricsCenter')) {
            const btnCenter = document.createElement('button');
            btnCenter.type = 'button';
            btnCenter.className = 'btnMonochromeLyricsCenter mediaButton paper-icon-button-light';
            btnCenter.title = 'Testi Karaoke (Apple Music)';
            btnCenter.innerHTML = '<span class="material-icons" style="font-size:22px;line-height:1;">mic</span>';
            btnCenter.addEventListener('click', (e) => {
                e.stopPropagation();
                toggleModal();
            });

            const nextBtn = center.querySelector('.nextTrackButton, .ButtonNextTrack');
            if (nextBtn && nextBtn.nextSibling) {
                center.insertBefore(btnCenter, nextBtn.nextSibling);
            } else {
                center.appendChild(btnCenter);
            }
        }

        // B. Dedicated button in .nowPlayingBarRight (in user data buttons or before volume)
        const right = document.querySelector('.nowPlayingBarRight');
        if (right && !right.querySelector('.btnMonochromeLyricsRight')) {
            const btnRight = document.createElement('button');
            btnRight.type = 'button';
            btnRight.className = 'btnMonochromeLyricsRight mediaButton paper-icon-button-light';
            btnRight.title = 'Testi Karaoke (Apple Music)';
            btnRight.innerHTML = '<span class="material-icons" style="font-size:22px;line-height:1;">lyrics</span>';
            btnRight.addEventListener('click', (e) => {
                e.stopPropagation();
                toggleModal();
            });

            const userButtons = right.querySelector('.nowPlayingBarUserDataButtons');
            if (userButtons) {
                userButtons.appendChild(btnRight);
            } else {
                right.prepend(btnRight);
            }
        }

        // C. Unhide and hijack Jellyfin native .openLyricsButton
        const nativeLyricsBtn = document.querySelector('.openLyricsButton');
        if (nativeLyricsBtn) {
            nativeLyricsBtn.classList.remove('hide');
            nativeLyricsBtn.style.display = 'inline-flex';
            nativeLyricsBtn.title = 'Testi Karaoke (Apple Music)';
            if (!nativeLyricsBtn.getAttribute('data-monochrome-hooked')) {
                nativeLyricsBtn.setAttribute('data-monochrome-hooked', 'true');
                nativeLyricsBtn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    e.preventDefault();
                    toggleModal();
                }, true);
            }
        }

        // D. Fullscreen page buttons (.nowPlayingPageUserDataButtons)
        const pageControls = document.querySelector('.nowPlayingPageUserDataButtons, .nowPlayingInfoControls');
        if (pageControls && !pageControls.querySelector('.btnMonochromeLyricsPage')) {
            const btnPage = document.createElement('button');
            btnPage.type = 'button';
            btnPage.className = 'btnMonochromeLyricsPage paper-icon-button-light';
            btnPage.title = 'Testi Karaoke (Apple Music)';
            btnPage.innerHTML = '<span class="material-icons" style="margin-right:6px;">mic</span> <span>Testi</span>';
            btnPage.addEventListener('click', (e) => {
                e.stopPropagation();
                toggleModal();
            });
            pageControls.appendChild(btnPage);
        }

        // E. Floating dock pill button (guaranteed visible on any layout or screen size)
        ensureFloatingPillButton(isAudioActive);

        // F. Hook Stop & Next buttons
        hookControlButtons();
    }

    function ensureFloatingPillButton(isAudioActive) {
        let pill = document.getElementById('monochromeLyricsFloatingPill');
        if (!pill) {
            pill = document.createElement('button');
            pill.id = 'monochromeLyricsFloatingPill';
            pill.type = 'button';
            pill.className = 'monochrome-lyrics-floating-pill';
            pill.title = 'Testi Karaoke (Apple Music)';
            pill.innerHTML = '<span class="pill-icon">🎤</span> <span class="pill-text">Testi</span>';
            pill.addEventListener('click', (e) => {
                e.stopPropagation();
                toggleModal();
            });
            document.body.appendChild(pill);
        }

        if (isAudioActive) {
            pill.classList.add('visible');
        } else {
            pill.classList.remove('visible');
        }
    }

    // 4. Hook Stop and Next buttons to prevent stop bugs and ensure skip
    function hookControlButtons() {
        // Stop buttons
        const stopButtons = document.querySelectorAll('.stopButton, .ButtonStop, button[title="Stop"], button[title="Ferma"]');
        stopButtons.forEach(btn => {
            if (btn.getAttribute('data-mono-stop-hooked')) return;
            btn.setAttribute('data-mono-stop-hooked', 'true');
            btn.addEventListener('click', () => {
                console.log('[Monochrome] Stop button clicked. Stopping audio and notifying server to cancel autoplay.');
                const audio = document.querySelector('audio');
                if (audio) {
                    audio.pause();
                    audio.currentTime = 0;
                }
                fetch('/Monochrome/Playback/Stop', { credentials: 'omit' }).catch(() => {});
            });
        });

        // Next buttons
        const nextButtons = document.querySelectorAll('.nextTrackButton, .ButtonNextTrack, button[title="Next"], button[title="Successivo"]');
        nextButtons.forEach(btn => {
            if (btn.getAttribute('data-mono-next-hooked')) return;
            btn.setAttribute('data-mono-next-hooked', 'true');
            btn.addEventListener('click', () => {
                setTimeout(() => {
                    const audio = document.querySelector('audio');
                    if (!audio || audio.paused || audio.ended) {
                        console.log('[Monochrome] Triggering backend Next track...');
                        fetch('/Monochrome/Playback/Next', { credentials: 'omit' }).catch(() => {});
                    }
                }, 350);
            });
        });
    }

    // 5. Fetch and render lyrics with search fallback
    async function loadLyrics() {
        const scrollContainer = document.getElementById('monochromeKaraokeScroll');
        if (!scrollContainer) return;

        const info = getCurrentTrackDetails();
        const dedupeKey = `${info.itemId}_${info.title}_${info.artist}`;

        if (currentLyrics && currentLoadedKey === dedupeKey) {
            return; // Already loaded for this track
        }

        scrollContainer.innerHTML = '<div class="monochrome-karaoke-empty">Ricerca testi sincronizzati in corso...</div>';

        let data = null;
        const endpoints = [];

        // Primary: Super-resilient search endpoint
        if (info.title) {
            const qTitle = encodeURIComponent(info.title);
            const qArtist = encodeURIComponent(info.artist || '');
            endpoints.push(`/Monochrome/Lyrics/Search?guid=${info.itemId}&title=${qTitle}&artist=${qArtist}`);
        }

        if (info.itemId) {
            endpoints.push(`/Monochrome/Tracks/ByGuid/${info.itemId}/Lyrics`);
            endpoints.push(`/Audio/${info.itemId}/Lyrics`);
        }

        for (const url of endpoints) {
            try {
                const res = await fetch(url, { headers: { 'Accept': 'application/json' } });
                if (res.ok) {
                    data = await res.json();
                    if (data && (data.lines?.length || data.Lyrics?.length || data.plainLyrics || data.rawLrc)) {
                        break;
                    }
                }
            } catch (e) { }
        }

        if (!data) {
            scrollContainer.innerHTML = `
                <div class="monochrome-karaoke-empty">
                    Nessun testo disponibile per "${info.title || 'questo brano'}".
                </div>`;
            currentLyrics = null;
            return;
        }

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
        currentLoadedKey = dedupeKey;
        lastActiveIdx = -1;

        if (lines.length === 0) {
            scrollContainer.innerHTML = '<div class="monochrome-karaoke-empty">Nessun testo sincronizzato disponibile.</div>';
            return;
        }

        // Render line items
        scrollContainer.innerHTML = '';
        lines.forEach((l, idx) => {
            const div = document.createElement('div');
            div.className = 'monochrome-lyric-line';
            div.id = `monoLyric_${idx}`;
            div.setAttribute('data-time', l.time);
            div.textContent = l.text;

            // Interactive seek-on-click
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

    // 6. Time sync loop for Apple Music lyrics
    function syncLyricsLoop() {
        requestAnimationFrame(syncLyricsLoop);

        if (!isModalOpen || !currentLyrics || currentLyrics.length === 0) {
            return;
        }

        const audio = document.querySelector('audio');
        if (!audio) return;

        const currentTime = audio.currentTime || 0;

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

    // 7. Track change listener
    function monitorPlaybackChanges() {
        const audio = document.querySelector('audio');
        if (audio) {
            audio.addEventListener('play', () => {
                updateModalHeader();
                if (isModalOpen) loadLyrics();
            });
            audio.addEventListener('loadedmetadata', () => {
                updateModalHeader();
                if (isModalOpen) loadLyrics();
            });
        }

        setInterval(() => {
            injectLyricsButtons();
            if (isModalOpen) {
                updateModalHeader();
                const info = getCurrentTrackDetails();
                const key = `${info.itemId}_${info.title}_${info.artist}`;
                if (key !== currentLoadedKey) {
                    loadLyrics();
                }
            }
        }, 1000);
    }

    // Initial boot
    ensureModalDOMElements();
    injectLyricsButtons();
    monitorPlaybackChanges();
    syncLyricsLoop();

})();

