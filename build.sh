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
VERSION="1.3.1.0"
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
        "version": "${VERSION}",
        "changelog": "Risolto definitivamente il problema della ricerca globale di Jellyfin che non mostrava canzoni: risoluzione reale del CollectionFolder genitore accessibile all'utente, superando il filtro di sicurezza FilterByUserAccessAsync di Jellyfin 12.0. Aggiornati retroattivamente tutti i brani memorizzati in precedenza. Sincronizzazione automatica delle ricerche nel Canale.",
        "targetAbi": "12.0.0.0",
        "sourceUrl": "https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/dist/${PLUGIN_NAME}_${VERSION}.zip",
        "checksum": "${MD5_CHECKSUM}",
        "timestamp": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")"
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
