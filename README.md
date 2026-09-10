# Jellyfin Monochrome Music Plugin (Jellyfin 12.0)

Plugin nativo per **Jellyfin 12.0** (.NET 10 / `net10.0`) per integrare, ascoltare ed esportare musica in alta definizione da **Monochrome Music** e dall'ecosistema TIDAL HiFi senza pubblicità e con streaming lossless.

---

## 🎵 Caratteristiche

- **Canale Jellyfin Nativo (`IChannel`)**:
  - Compare direttamente nella sezione **Canali** di Jellyfin.
  - Sfoglia i brani di tendenza e le classifiche (*Top Hits & Featured*).
  - Ricerca istantanea di brani, album e artisti.
  - Esplorazione completa degli album e delle discografie degli artisti.
  - Supporto per client ufficiali Jellyfin e lettori dedicati alla musica come **Finamp**, **Feishin**, **Symfonium**, **Manet** e **Jellyfin Web**.

- **Streaming Hi-Res & Lossless Dinamico**:
  - Risoluzione automatica dei flussi audio lossless (FLAC 16-bit / 44.1kHz e Hi-Res fino a 24-bit / 192kHz) e AAC (High/Low).
  - Risoluzione dinamica in tempo reale degli URL di riproduzione al click (nessun problema di scadenze token o link temporanei).
  - Modalità Direct Stream o Proxy Server integrato (utile per dispositivi dietro firewall o VPN).

- **Generazione e Sincronizzazione File `.strm`**:
  - Endpoint dedicato per esportare qualsiasi album direttamente nella cartella della tua libreria musicale locale di Jellyfin.
  - Crea la struttura `<CartellaMusica>/<Artista>/<Album>/01 - Titolo.strm` con download automatico della copertina ad alta risoluzione (`cover.jpg`).
  - Gli album esportati compaiono nella normale libreria **Musica** di Jellyfin insieme ai tuoi file locali!

- **Pannello di Configurazione Web (Dashboard Jellyfin)**:
  - Gestione dell'istanza API (default `https://monochrome.tf` o istanze personalizzate/self-hosted di *hifi-api-workers*).
  - Selezione della qualità audio preferita (`HI_RES_LOSSLESS`, `LOSSLESS`, `HIGH`, `LOW`).
  - Impostazione del Country Code del catalogo (es. `IT`, `US`, `GB`).
  - Connessione diretta o con token personalizzato.
  - Pulsante "Test Connection" integrato per testare la connessione in un click.

---

## 📦 Struttura del Progetto

```text
├── Api/
│   ├── Models.cs                  # Modelli DTO e deserializzatori JSON
│   └── MonochromeApiClient.cs     # Client HTTP per Monochrome/TIDAL e risoluzione manifest
├── Channels/
│   └── MonochromeChannel.cs       # Implementazione canale Jellyfin (IChannel & IRequiresMediaInfoCallback)
├── Configuration/
│   ├── PluginConfiguration.cs     # Modello configurazione XML
│   └── configPage.html            # Interfaccia grafica HTML/JS per la dashboard di Jellyfin
├── Controllers/
│   └── MonochromeController.cs    # Controller ASP.NET Core (/Monochrome/Stream, Search, ExportStrm, ecc.)
├── dist/                          # Pacchetti compilati (.zip, cartella e manifest.json)
├── build.sh                       # Script di compilazione e creazione pacchetto
├── Plugin.cs                      # Entrypoint del plugin (BasePlugin<T>)
├── PluginServiceRegistrator.cs    # Registrazione Dependency Injection Jellyfin
├── Jellyfin.Plugin.Monochrome.csproj # File di progetto .NET 10 (net10.0)
└── README.md                      # Questa documentazione
```

---

## 🚀 Installazione su Jellyfin 12.0

### Metodo 1: Installazione Manuale (Consigliato)

1. Compila il plugin (o usa la cartella già generata in `dist/`):
   ```bash
   ./build.sh
   ```
2. Troverai la cartella `dist/Jellyfin.Plugin.Monochrome_1.0.0.0`.
3. Copia il contenuto di questa cartella nella directory dei plugin del tuo server Jellyfin, ad esempio:
   - **Linux**: `/var/lib/jellyfin/plugins/Monochrome/` (oppure `~/.local/share/jellyfin/plugins/Monochrome/`)
   - **Docker**: `/<percorso-config-jellyfin>/plugins/Monochrome/`
   - **Windows**: `C:\ProgramData\Jellyfin\Server\plugins\Monochrome\`

   Esempio su Linux:
   ```bash
   sudo mkdir -p /var/lib/jellyfin/plugins/Monochrome
   sudo cp -r dist/Jellyfin.Plugin.Monochrome_1.0.0.0/* /var/lib/jellyfin/plugins/Monochrome/
   sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Monochrome/
   ```
4. Riavvia Jellyfin:
   ```bash
   sudo systemctl restart jellyfin
   ```

### Metodo 2: Tramite Repository Jellyfin (Consigliato)

Puoi aggiungere il plugin direttamente dal catalogo di Jellyfin tramite questo manifest repository:

1. Apri la **Dashboard di Jellyfin** -> **Plugin** -> scheda **Repository**.
2. Clicca su **+** per aggiungere un repository:
   - **Nome**: `Monochrome Music`
   - **URL Repository**: `https://raw.githubusercontent.com/PietroS-ITA/jellyfin-plugin-monochrome/main/manifest.json`
3. Clicca su **Salva**.
4. Vai nella scheda **Catalogo**, seleziona **Monochrome Music** e clicca **Installa**!

---

## ⚙️ Configurazione

1. Apri la **Dashboard di Jellyfin** -> **Plugin**.
2. Clicca su **Monochrome Music**.
3. Configura le opzioni:
   - **Monochrome / HiFi Instance URL**: URL dell'istanza (default: `https://monochrome.tf`).
   - **Preferred Audio Quality**:
     - `HI_RES_LOSSLESS`: FLAC a 24-bit fino a 192 kHz.
     - `LOSSLESS`: Qualità CD FLAC 16-bit / 44.1 kHz (consigliata per compatibilità e velocità).
     - `HIGH`: AAC 320 kbps.
     - `LOW`: AAC 96 kbps (risparmio dati).
   - **Catalogue Country Code**: `IT` (Italia) o altro codice a 2 lettere.
   - **Use Direct TIDAL API Connection**: Lasciare attivo per l'accesso diretto ad alte prestazioni.
   - **STRM Export Directory**: (Opzionale) Percorso del filesystem della tua cartella musica dove salvare gli album virtuali `.strm`.
4. Clicca su **Test Connection** per verificare che la connessione risponda correttamente.
5. Clicca su **Save**.

---

## 🎧 Utilizzo

### 1. Ascolto dal Canale
Nel menu principale di Jellyfin, seleziona **Canali** -> **Monochrome Music**:
- Apri **Top Hits & Featured Tracks** per ascoltare subito la musica più popolare.
- Apri **Search Music** per cercare i tuoi artisti e album preferiti.
- Clicca su qualsiasi brano per avviare la riproduzione in alta qualità lossless!

### 2. Esportazione Album come `.strm` (Libreria Musicale Locale)
Puoi aggiungere gli album di Monochrome direttamente nella tua libreria musicale nativa eseguendo una richiesta POST all'endpoint del plugin:

```bash
curl -X POST "http://localhost:8096/Monochrome/ExportStrm?albumId=1550544&targetDirectory=/percorso/tua/musica"
```
Il plugin creerà la cartella dell'album, scaricherà la copertina e genererà i file `.strm`. Una volta rieseguita la scansione della libreria in Jellyfin, l'album sarà visualizzato e riproducibile come se fosse presente sull'hard disk!

### 3. Endpoint API Disponibili

| Metodo | Endpoint | Descrizione |
|---|---|---|
| `GET` | `/Monochrome/Stream?trackId={id}` | Risolve lo stream audio e reindirizza (302) o fa da proxy |
| `GET` | `/Monochrome/Search?q={query}` | Ricerca brani, album e artisti |
| `GET` | `/Monochrome/Album?id={albumId}` | Metadati album e tracce |
| `GET` | `/Monochrome/Artist?id={artistId}` | Metadati artista, album e top tracks |
| `POST` | `/Monochrome/ExportStrm?albumId={id}` | Esporta album in file `.strm` nella cartella musica |
| `GET` | `/Monochrome/TestConnection` | Esegue un test diagnostico di connessione |

---

## 🛠️ Ricompilazione

Il plugin può essere ricompilato ed esteso con:
```bash
./build.sh
```
I file compilati e il pacchetto `.zip` verranno generati automaticamente nella cartella `dist/`.

