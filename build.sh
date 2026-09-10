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
VERSION="${1:-1.3.7.0}"
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
        "version": "1.3.7.0",
        "changelog": "Risolto troncamento a 30 secondi e skip avanti alla canzone successiva: 1) Risoluzione audio a durata INTERA (FLAC lossless da 3+ minuti anziche preview di 29.9s) tramite istanza worker HiFi dedicata con retry resiliente dei segmenti DASH. 2) Skip avanti / autoplay radio automatico: attivazione controlli multimediali di sessione, accodamento PlayLast e avanzamento automatico su PlaybackStopped. 3) Pre-caching in background delle tracce successive per passaggio istantaneo senza attese. 4) Pulizia automatica all'avvio delle vecchie anteprime da 30s (< 4MB) in cache.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_1.3.7.0.zip",
        "checksum": "${MD5_CHECKSUM}",
        "timestamp": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
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
