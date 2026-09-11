#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Detect dotnet binary
if command -v dotnet >/dev/null 2>&1; then
    DOTNET_BIN="dotnet"
elif [ -f "$HOME/.dotnet/dotnet" ]; then
    DOTNET_BIN="$HOME/.dotnet/dotnet"
else
    echo "Error: dotnet SDK not found in PATH or ~/.dotnet/dotnet"
    exit 1
fi

echo "=== Building Jellyfin.Plugin.Monochrome with $($DOTNET_BIN --version) ==="
"$DOTNET_BIN" build -c Release

OUTPUT_DIR="$SCRIPT_DIR/bin/Release/net10.0"
DIST_DIR="$SCRIPT_DIR/dist"
PLUGIN_NAME="Jellyfin.Plugin.Monochrome"
VERSION="${1:-1.3.9.5}"
PACKAGE_DIR="$DIST_DIR/${PLUGIN_NAME}_${VERSION}"

mkdir -p "$PACKAGE_DIR"

echo "=== Packaging plugin into $PACKAGE_DIR ==="
cp "$OUTPUT_DIR/${PLUGIN_NAME}.dll" "$PACKAGE_DIR/"
cp "$OUTPUT_DIR/${PLUGIN_NAME}.pdb" "$PACKAGE_DIR/"
cp "$OUTPUT_DIR/${PLUGIN_NAME}.deps.json" "$PACKAGE_DIR/"

# Create zip archive for distribution
cd "$DIST_DIR"
zip -r "${PLUGIN_NAME}_${VERSION}.zip" "${PLUGIN_NAME}_${VERSION}" >/dev/null
rm -rf "$PACKAGE_DIR"

# Generate SHA256 and MD5 checksums
SHA256_CHECKSUM=$(sha256sum "${PLUGIN_NAME}_${VERSION}.zip" | awk '{print $1}')
MD5_CHECKSUM=$(md5sum "${PLUGIN_NAME}_${VERSION}.zip" | awk '{print $1}')

# Create Jellyfin repository manifest
cat <<EOF > "$DIST_DIR/manifest.json"
[
  {
    "guid": "0a9d15e2-6bf3-4674-8b1b-7a329d7831d1",
    "name": "Monochrome Music",
    "description": "Stream and browse lossless Hi-Res music from Monochrome and TIDAL in Jellyfin 12.0.",
    "overview": "Monochrome Music provider and channel for Jellyfin 12.0",
    "owner": "PietroS-ITA",
    "category": "Live TV & Channels / Music",
    "versions": [
      {
        "version": "1.3.9.5",
        "changelog": "Novita Testi Karaoke & Mobile: 1) Cache-busting automatico (?v=1.3.9.5) su script e fogli di stile iniettati in index.html e JavaScript Injector: aggiornamento istantaneo senza svuotamento manuale cache browser. 2) Intercettazione automatica rotta nativa Jellyfin (#/lyrics): apertura istantanea modale Apple Music a tutto schermo evitando il blocco dello scorrimento touch nativo. 3) Scorrimento continuo fluido e auto-recentering garantito con min-height:0 su flex container e viewport dinamico (100dvh). 4) Mini-pulsante microfono integrato direttamente nella barra compatta mobile e hijack di ogni comando testi nativo.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.9.5.zip",
        "checksum": "${MD5_CHECKSUM}",
        "timestamp": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
      },
      {
        "version": "1.3.9.4",
        "changelog": "Novita Testi Karaoke e Mobile: 1) Risolto lo scorrimento dei testi (auto-scroll e sincronizzazione continua): rileva accuratamente il playback time da qualsiasi riproduttore HTML5 (audio, video, DASH) e da Jellyfin PlaybackManager; rimosso conflitto smooth-scroll CSS/JS e riallineamento istantaneo/fluido a centro schermo ad ogni cambio strofa. 2) Supporto Completo Mobile: sovrascritti i selettori CSS di Jellyfin che nascondevano i pulsanti su smartphone (.layout-mobile); aggiunto pulsante Testi nei controlli principali a schermo intero (.nowPlayingInfoButtons) e badge (.btnMonochromeLyricsBadge); pillola flottante ottimizzata con z-index 999999 e posizionamento sicuro. 3) Supporto esteso a tutti i formati di risposta testi (LRC raw con parser regex client-side, plainLyrics, Lyrics e lines/Lines).",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.9.4.zip",
        "checksum": "ecd425eb01454cd45b23bebfb5418150",
        "timestamp": "2026-09-11T08:21:36Z"
      },
      {
        "version": "1.3.9.3",
        "changelog": "Novita Testi Karaoke Apple Music Sing: 1) Animazione progressiva continua lettera-per-lettera e parola-per-parola (60/120fps) con riempimento fluido a gradiente in tempo reale sincronizzato con la voce. 2) Effetto rimbalzo dinamico (bounce) e luminescenza fluorescente sulla parola cantata. 3) Supporto sia a Enhanced LRC (<mm:ss.xx> per parola) sia interpolazione automatica con pesatura proporzionale dei caratteri per LRC standard. 4) Sfondo copertina dinamico con animazione fluida 'breathing' e sfocatura Apple Music. 5) Interazione click-to-seek sia su riga che su singola parola.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.9.3.zip",
        "checksum": "babcd218bda1d76b0a98c797407b8988",
        "timestamp": "2026-09-11T07:38:55Z"
      },
      {
        "version": "1.3.9.2",
        "changelog": "1) Risolto definitivamente il problema del tasto Stop: azzeramento immediato e totale di tutte le code radio su stop manuale e prevenzione autoplay se il brano e interrotto prima della fine fisica reale (entro 2.5s). 2) Supporto nativo integrato ai Testi (MediaStreamType.Lyric e generazione file .lrc): Jellyfin ora mostra il pulsante testi sincronizzati nativo su Web, Mobile, Android, iOS e Desktop senza bisogno di plugin esterni. 3) Supporto e auto-registrazione dinamica con il plugin 'JavaScript Injector' per UI Apple Music e pillola flottante.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.9.2.zip",
        "checksum": "f7687c44b09ff1ac1483af4b263e056b",
        "timestamp": "2026-09-10T23:16:44Z"
      },
      {
        "version": "1.3.9.1",
        "changelog": "Risolto definitivamente posizionamento pulsante Testi nella barra (inserito nei controlli centrali, controlli utente a destra, pagina schermo intero e dock pill galleggiante); ricerca testi ultra-resiliente multi-sorgente con fallback su titolo e artista; fix totale del tasto Stop (rimossa propagazione client-queue e cancellazione istantanea coda server su stop).",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.9.1.zip",
        "checksum": "a1077ca4d5527d28328a1afa01e75fa6",
        "timestamp": "2026-09-10T22:20:24Z"
      },
      {
        "version": "1.3.9.0",
        "changelog": "Novita e correzioni: 1) Testi Karaoke in stile Apple Music sincronizzati nel tempo con pulsante 'Testi' ben visibile nella barra di riproduzione, sfocatura dinamica della copertina, evidenziazione riga attiva e seek interattivo. 2) Accuratezza della ricerca migliorata drasticamente per query composte da Titolo + Artista (es. 'Magnetic The bausa'): punteggio di rilevanza massimo (100) per match esatto combinato. 3) Risolto problema del pulsante Stop (non avvia piu un'altra canzone quando fermato manualmente) e migliorata l'affidabilita del pulsante Next.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.9.0.zip",
        "checksum": "c3e3eae966071e7aa2f707b493eef479",
        "timestamp": "2026-09-10T21:59:49Z"
      },
      {
        "version": "1.3.8.0",
        "changelog": "Risolto definitivamente il blocco a metà / 30 secondi e il salto al brano successivo: 1) Remuxing istantaneo dei segmenti DASH fMP4 in contenitore nativo FLAC/M4A via FFmpeg (10ms): abilita DirectPlay nativo al 100% nei browser senza transcodifica live MPEG-TS che interrompeva l'audio. 2) Supporto seek / avanzamento rapido su tutta la durata della traccia tramite byte-range HTTP 206. 3) Coda radio/autoplay persistente tra sessioni e pre-caching per skip istantaneo. 4) Auto-conversione e migrazione del database e della cache locale all'avvio.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.8.0.zip",
        "checksum": "e5e57324a82fa14a21137d65af95a07e",
        "timestamp": "2026-09-10T21:24:50Z"
      },
      {
        "version": "1.3.7.0",
        "changelog": "Risolto troncamento a 30 secondi e skip avanti alla canzone successiva: 1) Risoluzione audio a durata INTERA (FLAC lossless da 3+ minuti anziche preview di 29.9s) tramite istanza worker HiFi dedicata con retry resiliente dei segmenti DASH. 2) Skip avanti / autoplay radio automatico: attivazione controlli multimediali di sessione, accodamento PlayLast e avanzamento automatico su PlaybackStopped. 3) Pre-caching in background delle tracce successive per passaggio istantaneo senza attese. 4) Pulizia automatica all'avvio delle vecchie anteprime da 30s (< 4MB) in cache.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.7.0.zip",
        "checksum": "b3545280eed327187c315a05ebb120ff",
        "timestamp": "2026-09-10T19:19:05Z"
      },
      {
        "version": "1.3.6.0",
        "changelog": "Risolto errore riproduzione 'file non supportato' su tutti i client e corretta visualizzazione artista/titolo: 1) Popolazione automatica dei MediaStreamInfos per i brani Audio e supporto sia per direct play che transcodifica su file in cache. 2) Registrazione nativa degli artisti (MusicArtist) e rimozione automatica del campo Album per i singoli, garantendo che nei risultati di ricerca compaia il nome dell'artista sotto il titolo anziche il titolo del singolo. 3) Auto-riparazione SQLite all'avvio su jellyfin.db per tutti i brani memorizzati in precedenza.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.6.0.zip",
        "checksum": "0d206f654b9f29a0081dcfb2f8158d9a",
        "timestamp": "2026-09-10T17:18:22Z"
      },
      {
        "version": "1.3.5.0",
        "changelog": "Risolto definitivamente il problema del singolo risultato e del salvataggio impostazioni: 1) Assegnazione di PresentationUniqueKey deterministica a brani, album e artisti, impedendo a ApplyGroupingFilter di Jellyfin di collassare tutti i risultati in un unico elemento. 2) Auto-riparazione SQLite all'avvio su jellyfin.db per correggere gli elementi preesistenti. 3) Corretto il pannello impostazioni (supporto camelCase / PascalCase e evento viewshow per la SPA Jellyfin Web). 4) Canale Monochrome disattivato di default per non intasare la home. 5) Autoplay radio automatico stile Spotify al termine del singolo brano.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.5.0.zip",
        "checksum": "6b6321949b814a2bbfe35bd75f314558",
        "timestamp": "2026-09-10T14:52:22Z"
      },
      {
        "version": "1.3.4.0",
        "changelog": "Risoluzione libreria fisica (PhysicalFolderIds) tramite viste utente e cartelle virtuali per filtro TopParentId di FilterByUserAccessAsync in Jellyfin 12.0.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.4.0.zip",
        "checksum": "d135ea5fe9fc0ee4d2e87c0ff569d675",
        "timestamp": "2026-09-10T14:10:00Z"
      },
      {
        "version": "1.3.3.0",
        "changelog": "Esperienza streaming stile Spotify: 1) Monochrome Music rimosso dalla Home e dai tab superiori (canale disattivato di default). 2) Ricerca globale completa con risultati multipli (brani prioritari 0.98f, album 0.95f, artisti 0.90f) superando il filtro TopParentId di Jellyfin 12.0. 3) Salvataggio nei Preferiti (♥) e Playlist utente senza salvare file fisici su disco. 4) Autoplay radio e Instant Mix automatico basato su stile e genere tramite ILocalSimilarItemsProvider e TIDAL Radio.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.3.0.zip",
        "checksum": "af3cf8248cfc02908fb74dfb5c03008b",
        "timestamp": "2026-09-10T13:40:29Z"
      },
      {
        "version": "1.3.2.0",
        "changelog": "Risolto definitivamente il problema della ricerca globale: risoluzione nativa del Canale 'Monochrome Music' come TopParentId per l'accesso utente (superando il filtro di sicurezza FilterByUserAccessAsync in assenza di una libreria Musica locale), esecuzione fully asynchronous del search provider senza deadlock. Sostituite le immagini con icone ad alta risoluzione e copertine Tidal funzionanti (eliminando le icone '?').",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.2.0.zip",
        "checksum": "d4f5a0f4145ae8585bef5b7110e7d783",
        "timestamp": "2026-09-10T13:20:14Z"
      },
      {
        "version": "1.3.1.0",
        "changelog": "Risolto definitivamente il problema della ricerca globale di Jellyfin che non mostrava canzoni: risoluzione reale del CollectionFolder genitore accessibile all'utente, superando il filtro di sicurezza FilterByUserAccessAsync di Jellyfin 12.0. Aggiornati retroattivamente tutti i brani memorizzati in precedenza. Sincronizzazione automatica delle ricerche nel Canale.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.1.0.zip",
        "checksum": "3a1458c01af8930214133f4286986312",
        "timestamp": "2026-09-10T12:54:18Z"
      },
      {
        "version": "1.3.0.0",
        "changelog": "Risolto l'errore di riproduzione 'file non supportato': implementato il download e la concatenazione sequenziale dei segmenti fMP4/FLAC con caching locale, abilitando riproduzione fluida, transcodifica e seeking per tutti i client.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.0.0.zip",
        "checksum": "d6e3a170b0ed9880893736af89769781",
        "timestamp": "2026-09-10T11:03:01Z"
      },
      {
        "version": "1.2.0.0",
        "changelog": "Integrazione nativa con la ricerca globale di Jellyfin 12.0 (ISearchProvider, IExternalSearchProvider, IMediaSourceProvider) accessibile a tutti gli utenti (base e admin). Rimosse tab e restrizioni di amministrazione.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.2.0.0.zip",
        "checksum": "e09425802cc567f42d1a09ac664a44ea",
        "timestamp": "2026-09-10T09:50:11Z"
      },
      {
        "version": "1.1.0.0",
        "changelog": "Aggiunta interfaccia di ricerca libera e sincronizzazione ricerche nel Canale.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/Jellyfin.Plugin.Monochrome_1.1.0.0.zip",
        "checksum": "00fe6b759e738b6183763bfc085050ca",
        "timestamp": "2026-09-10T09:22:09Z"
      },
      {
        "version": "1.0.0.0",
        "changelog": "Initial release for Jellyfin 12.0 (.NET 10). Supports native Channel browsing, direct FLAC/Lossless streaming, STRM export, and REST API controller.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/Jellyfin.Plugin.Monochrome_1.0.0.0.zip",
        "checksum": "4d9a35a43dadd8de65947652f2306729",
        "timestamp": "2026-09-10T10:00:00Z"
      }
    ]
  }
]
EOF

cp "$DIST_DIR/manifest.json" "$SCRIPT_DIR/manifest.json"

echo ""
echo "=== Build and packaging completed successfully! ==="
echo "Plugin folder: $PACKAGE_DIR"
echo "Plugin archive: $DIST_DIR/${PLUGIN_NAME}_${VERSION}.zip"
echo "SHA256: $SHA256_CHECKSUM"
echo "Manifest: $DIST_DIR/manifest.json"
