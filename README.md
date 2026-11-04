# Jellyfin Emby Federation & Bridge Plugin (`Jellyfin.Plugin.Emby`)

[![CI](https://github.com/FaserF/emby-jellyfin/actions/workflows/ci.yml/badge.svg)](https://github.com/FaserF/emby-jellyfin/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/FaserF/emby-jellyfin?color=52B54B&logo=github)](https://github.com/FaserF/emby-jellyfin/releases)
[![Target Jellyfin](https://img.shields.io/badge/Jellyfin-10.10.x%20%7C%2012.x-00A4DC?logo=jellyfin)](https://jellyfin.org/)
[![Target .NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A universal, production-grade Jellyfin plugin that **federates remote Emby servers directly into Jellyfin**. It provides seamless catalog synchronization via `.strm` direct-play files and rich `.nfo` metadata sidecars (with TMDb, TVDb, and IMDb IDs), a zero-buffer HTTP Range stream proxy, bi-directional watch state and favorite synchronization, per-user account mapping, and an intelligent **Seerr / Suggestarr Request Guard** that automatically prevents duplicate media requests when items are already available on Emby.

---

## 🌟 Key Features

* **📁 Automatic Library Federation**:
  * Synchronizes Movies, TV Series, Seasons, and Episodes from any remote Emby server into Jellyfin.
  * Writes `.strm` stream pointers and `.nfo` metadata sidecars with full TMDb (`<tmdbid>`), TVDb (`<tvdbid>`), and IMDb (`<imdbid>`) IDs plus `<fileinfo><streamdetails>` (video resolution, codec, audio channels).
  * Automatically recognized by Jellyfin, **Seerr** (`jellyseerr`), and **Suggestarr** as **AVAILABLE** without double-scanning or downloading media files.
* **⚡ Zero-Buffer Direct Playback & Stream Proxy**:
  * Pointers route directly to the high-speed remote Emby stream API (`/emby/Videos/{id}/stream?static=true`).
  * Supports both direct URL playback (client connects directly to Emby) and Jellyfin server-side HTTP Range stream proxy (`/api/embyPlugin/stream/{id}`).
  * Full audio track and subtitle passthrough with no server transcoding overhead.
* **📺 Native Emby Channel**:
  * Browse the remote Emby server's movie and series catalog live inside Jellyfin's Web, TV, and Mobile apps.
* **⏱️ Bi-Directional Watch Progress & Favorite Sync**:
  * Playhead progress and watched status sync between Jellyfin and Emby in real-time.
  * Toggling Jellyfin Favorites (♥️) marks items as favorites on Emby.
* **👥 Multi-User Account Mapping**:
  * Map individual Jellyfin users to their own personal Emby credentials for personalized watch history.
  * Unlinked users can fall back to the primary/shared Emby server connection (or be isolated).
* **🛡️ Seerr & Suggestarr Request Guard**:
  * Built-in availability check (`/api/embyPlugin/check-availability`) and webhook endpoint (`/api/embyPlugin/webhook/seerr`).
  * When a user requests a movie or show in Seerr/Suggestarr that exists on the federated Emby server, the request is automatically declined with a friendly reason and direct web link to stream it immediately on Emby.
* **🟢 Emby Hub Web Client Modal**:
  * Client script (`embyPlugin.js`) injects a convenient **🟢 Emby Hub** button into the Jellyfin header for live catalog searches, stream triggering, and one-click sync.

---

## 🚀 Installation

### Option A: Via Jellyfin Plugin Repository (Recommended)

1. In the Jellyfin Dashboard, navigate to **Plugins** $\rightarrow$ **Repositories**.
2. Click **+** and add:
   * **Repository Name**: `Emby Federation Plugin`
   * **Repository URL**: `https://raw.githubusercontent.com/FaserF/emby-jellyfin/main/manifest.json`
3. Go to the **Catalog** tab, select **Emby Federation & Bridge**, and click **Install**.
4. Restart your Jellyfin server.

### Option B: Manual Installation

1. Download the latest `emby.zip` release from [GitHub Releases](https://github.com/FaserF/emby-jellyfin/releases).
2. Extract the archive into your Jellyfin plugins directory:
   * **Linux / Docker**: `/config/plugins/Emby/` (or `/config/data/plugins/Emby/`)
   * **Windows**: `%ProgramData%\Jellyfin\Server\plugins\Emby\`
3. Restart Jellyfin.

---

## ⚙️ Configuration

Open your Jellyfin Dashboard and click **Emby Federation & Bridge** under *Plugins*.

### 1. Remote Emby Server Connection
* **Emby Server URL**: e.g., `https://emby.example.com`
* **Emby Username & Password**: Your credentials on the remote server.
* Click **Test & Authenticate Connection** to verify access and auto-generate the API access token.

### 2. Library Federation & Storage
* **Federated Library Base Path**: Directory where `.strm` files and `.nfo` sidecars will be stored (default: `/data/emby`). Subdirectories `movies` and `tv` are created automatically.
* **Attach to Existing Libraries**: Merge into your existing Jellyfin `Filme` and `Serien` library folders or create dedicated libraries.
* **Direct Stream URLs**: When enabled, `.strm` files stream directly from the Emby server with zero hops through Jellyfin.

### 3. Environment Variables & Seed Configuration (Zero-Secret Deployments)
For automated Docker deployments, you can configure the plugin via environment variables or a seed JSON file without storing any secrets in version control:

| Environment Variable | Description |
|---|---|
| `EMBY_PEER_URL` | Remote Emby server URL (e.g. `https://emby.example.com`) |
| `EMBY_PEER_USERNAME` | Remote Emby username |
| `EMBY_PEER_PASSWORD` | Remote Emby password |
| `EMBY_PEER_API_KEY` | Optional pre-generated Emby user access token |

Alternatively, place an optional seed file at `/config/data/plugins/Emby/emby_seed.json`:
```json
{
  "serverUrl": "https://emby.example.com",
  "username": "user",
  "password": "secretpassword"
}
```

---

## 🛡️ Seerr / Overseerr / Suggestarr Integration

To prevent users from requesting movies or TV series that are already available on the federated Emby server:

1. In **Seerr / Jellyseerr / Overseerr**, go to **Settings** $\rightarrow$ **Notifications** $\rightarrow$ **Webhook**.
2. Set the **Webhook URL** to:
   ```text
   http://<your-jellyfin-host>:8096/api/embyPlugin/webhook/seerr
   ```
3. Enable the **Request Pending Approval** and **Request Automatically Approved** notification triggers.
4. If an item is already on the Emby server, the plugin automatically marks it, cancels pending downloads in Sonarr/Radarr, and notifies the requester with the direct Emby link.

---

## 🛠️ Building & Development

### Prerequisites
* [.NET 10.0 SDK](https://dotnet.microsoft.com/)
* Docker (optional, for containerized builds)

### Build & Run Tests
```bash
# Restore and build
dotnet restore
dotnet build -c Release

# Run unit test suite
dotnet test -c Release
```

### Create Release Archive
```bash
mkdir -p dist
cp Jellyfin.Plugin.Emby/bin/Release/net10.0/Jellyfin.Plugin.Emby.dll dist/
cp Jellyfin.Plugin.Emby/bin/Release/net10.0/Jellyfin.Plugin.Emby.pdb dist/
cp Jellyfin.Plugin.Emby/*.png dist/ 2>/dev/null || true
cp meta.json dist/ 2>/dev/null || true
cd dist && zip -9 -r ../emby.zip . && cd ..
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
