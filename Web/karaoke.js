(function () {
    'use strict';

    console.log('[Monochrome] Apple Music Karaoke & Lyrics client v1.3.9.3 loaded.');

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

    // Helper: Parse words and timestamps (Enhanced LRC tags or character-weighted distribution)
    function parseLineWords(rawText, lineStartTime, lineEndTime) {
        if (!rawText) return [];

        // Check for Enhanced LRC tags: <mm:ss.xx>word
        const enhancedTagRegex = /<(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?>/g;
        if (enhancedTagRegex.test(rawText)) {
            enhancedTagRegex.lastIndex = 0;
            const parts = [];
            let match;
            while ((match = enhancedTagRegex.exec(rawText)) !== null) {
                const mins = parseInt(match[1], 10);
                const secs = parseInt(match[2], 10);
                let ms = 0;
                if (match[3]) {
                    let msStr = match[3];
                    if (msStr.length === 2) msStr += '0';
                    ms = parseInt(msStr, 10);
                }
                const tagTime = (mins * 60) + secs + (ms / 1000.0);
                if (parts.length > 0) parts[parts.length - 1].end = tagTime;

                const afterTagIdx = match.index + match[0].length;
                const nextMatch = rawText.slice(afterTagIdx).match(/<(\d{1,2}):(\d{2})/);
                const wordEndIdx = nextMatch ? afterTagIdx + nextMatch.index : rawText.length;
                const wordText = rawText.slice(afterTagIdx, wordEndIdx).trim();

                if (wordText) {
                    parts.push({ text: wordText, start: tagTime, end: tagTime + 0.6 });
                }
            }
            if (parts.length > 0) {
                parts[parts.length - 1].end = Math.max(parts[parts.length - 1].start + 0.4, lineEndTime);
                return parts;
            }
        }

        // Standard LRC: Distribute word timings proportionally based on character count
        const cleanText = rawText.replace(/<[^>]+>/g, '').trim();
        const rawWords = cleanText.split(/\s+/).filter(w => w.length > 0);
        if (rawWords.length === 0) return [];

        const rawDuration = Math.max(0.6, lineEndTime - lineStartTime);
        const estimatedSungDuration = Math.min(rawDuration, Math.max(1.5, cleanText.length * 0.18));
        const totalChars = rawWords.reduce((sum, w) => sum + Math.max(1, w.length), 0);

        let curTime = lineStartTime;
        return rawWords.map((word) => {
            const charWeight = Math.max(1, word.length);
            const wordDur = (charWeight / totalChars) * estimatedSungDuration;
            const wStart = curTime;
            const wEnd = wStart + wordDur;
            curTime = wEnd;
            return {
                text: word,
                start: Math.round(wStart * 100) / 100,
                end: Math.round(wEnd * 100) / 100
            };
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

        if (lines.length === 0) {
            scrollContainer.innerHTML = '<div class="monochrome-karaoke-empty">Nessun testo sincronizzato disponibile.</div>';
            currentLyrics = null;
            return;
        }

        // Calculate line end times and individual word timings for Apple Music syllable fill
        lines.forEach((l, idx) => {
            const nextTime = (idx < lines.length - 1) ? lines[idx + 1].time : (l.time + 4.0);
            const rawDur = Math.max(0.5, nextTime - l.time);
            const clean = (l.text || '').replace(/<[^>]+>/g, '').trim();
            const estDur = Math.min(rawDur, Math.max(1.5, clean.length * 0.18));
            l.endTime = l.time + estDur;
            l.words = parseLineWords(l.text, l.time, l.endTime);
        });

        currentLyrics = lines;
        currentLoadedKey = dedupeKey;
        lastActiveIdx = -1;

        // Render line items with word spans
        scrollContainer.innerHTML = '';
        lines.forEach((l, idx) => {
            const div = document.createElement('div');
            div.className = 'monochrome-lyric-line';
            div.id = `monoLyric_${idx}`;
            div.setAttribute('data-time', l.time);

            if (l.words && l.words.length > 0) {
                l.words.forEach((w, wIdx) => {
                    const span = document.createElement('span');
                    span.className = 'mono-word';
                    span.id = `monoWord_${idx}_${wIdx}`;
                    span.textContent = w.text;
                    span.setAttribute('data-start', w.start);
                    span.setAttribute('data-end', w.end);
                    span.addEventListener('click', (e) => {
                        e.stopPropagation();
                        seekAudioTo(w.start);
                    });
                    div.appendChild(span);
                    if (wIdx < l.words.length - 1) {
                        div.appendChild(document.createTextNode(' '));
                    }
                });
            } else {
                div.textContent = (l.text || '').replace(/<[^>]+>/g, '').trim();
            }

            // Interactive seek-on-click for line
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

    // 6. Time sync loop for Apple Music lyrics (Word-by-word & Letter-by-letter live fill)
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
            if (currentLyrics[i].time <= currentTime + 0.12) {
                activeIdx = i;
                break;
            }
        }

        // A. Handle line changes (active/passed states & smooth auto-centering)
        if (activeIdx !== lastActiveIdx) {
            lastActiveIdx = activeIdx;
            const scrollContainer = document.getElementById('monochromeKaraokeScroll');

            currentLyrics.forEach((_, idx) => {
                const el = document.getElementById(`monoLyric_${idx}`);
                if (!el) return;

                if (idx === activeIdx) {
                    el.classList.add('active');
                    el.classList.remove('passed');
                } else if (idx < activeIdx) {
                    el.classList.remove('active');
                    el.classList.add('passed');
                    el.style.removeProperty('--line-progress');
                    // Mark passed words as 100% lit
                    const words = el.querySelectorAll('.mono-word');
                    words.forEach(w => {
                        w.classList.remove('word-active');
                        w.classList.add('word-passed');
                        w.style.setProperty('--word-progress', '100%');
                    });
                } else {
                    el.classList.remove('active', 'passed');
                    el.style.removeProperty('--line-progress');
                    // Reset upcoming words
                    const words = el.querySelectorAll('.mono-word');
                    words.forEach(w => {
                        w.classList.remove('word-active', 'word-passed');
                        w.style.setProperty('--word-progress', '0%');
                    });
                }
            });

            if (activeIdx >= 0 && !userHasScrolled && scrollContainer) {
                const activeEl = document.getElementById(`monoLyric_${activeIdx}`);
                if (activeEl) {
                    const targetScroll = activeEl.offsetTop - (scrollContainer.clientHeight * 0.38);
                    scrollContainer.scrollTo({
                        top: Math.max(0, targetScroll),
                        behavior: 'smooth'
                    });
                }
            }
        }

        // B. Continuous letter-by-letter & word-by-word animation on active line (60/120fps)
        if (activeIdx >= 0 && currentLyrics[activeIdx]) {
            const activeLine = currentLyrics[activeIdx];
            const activeEl = document.getElementById(`monoLyric_${activeIdx}`);
            if (activeEl) {
                const lineStart = activeLine.time;
                const lineEnd = activeLine.endTime || (lineStart + 3.5);
                const lineDur = Math.max(0.1, lineEnd - lineStart);
                const lineProg = Math.max(0, Math.min(1, (currentTime - lineStart) / lineDur));
                activeEl.style.setProperty('--line-progress', (lineProg * 100).toFixed(1) + '%');

                if (activeLine.words && activeLine.words.length > 0) {
                    activeLine.words.forEach((w, wIdx) => {
                        const wEl = document.getElementById(`monoWord_${activeIdx}_${wIdx}`);
                        if (!wEl) return;

                        if (currentTime >= w.end) {
                            // Word passed: fully illuminated solid white
                            if (!wEl.classList.contains('word-passed')) {
                                wEl.classList.remove('word-active');
                                wEl.classList.add('word-passed');
                            }
                            wEl.style.setProperty('--word-progress', '100%');
                        } else if (currentTime >= w.start) {
                            // Word currently being sung: progressive fill & glowing lift
                            if (!wEl.classList.contains('word-active')) {
                                wEl.classList.add('word-active');
                                wEl.classList.remove('word-passed');
                            }
                            const wDur = Math.max(0.04, w.end - w.start);
                            const wProg = Math.max(0, Math.min(1, (currentTime - w.start) / wDur));
                            wEl.style.setProperty('--word-progress', (wProg * 100).toFixed(1) + '%');
                        } else {
                            // Word upcoming: translucent
                            if (wEl.classList.contains('word-active') || wEl.classList.contains('word-passed')) {
                                wEl.classList.remove('word-active', 'word-passed');
                            }
                            wEl.style.setProperty('--word-progress', '0%');
                        }
                    });
                }
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

