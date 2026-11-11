<p align="center"><img src="Assets/logo.png" width="96" alt="BetterLC logo"></p>

<h1 align="center">BetterLC</h1>

<p align="center">A privacy-focused Windows launcher for Lunar Client.</p>

---

BetterLC starts Lunar Client without Lunar's own launcher and cuts off the data Lunar collects about you and your PC. It's one 27 MB executable with no installer. Your normal Lunar installation is left untouched.

- [What Lunar Client collects](#what-lunar-client-collects)
- [What BetterLC does about it](#what-betterlc-does-about-it)
- [What BetterLC can't hide](#what-betterlc-cant-hide)
- [Using BetterLC](#using-betterlc)
- [Checking our work](#checking-our-work)

## What Lunar Client collects

Everything below comes from Lunar Client's own game files (`lunar.jar` and the `genesis` bootstrap jar, both downloaded from Lunar's servers) and from recording what the game actually sends. Identifiers in `code` are class, message or field names you can find in those files yourself; [Checking our work](#checking-our-work) shows how.

### 1. A device profile on every connection

The game holds a WebSocket open to `websocket.lunarclientprod.com`. The first thing it sends is a `Handshake` message. In a recorded session it was **11.7 KB**. Its fields include:

| Field | What it is |
|---|---|
| `installation_id` | A per-install identifier |
| `hwid` | A hardware ID (defined but not filled in by current builds) |
| `operating_system`, `operating_system_release`, `cpu_architecture` | Your OS and CPU |
| `overwolf_muid` | An Overwolf machine ID (sent in our recording) |
| `canary_token` | A token from Lunar's launch API that ties this session to the launch request |
| `language` | Your game language |
| `installed_mods` | Every installed mod: id, name, version and type |
| `ichor_modules` | Lunar's own modules in use |
| `gl_extensions` | Your graphics driver's full OpenGL extension list (a strong fingerprint) |
| `nvidia_memory`, `mesa_memory` | Your GPU's video memory |
| `settings` | Your client settings |
| `location` | Where you are in the game |

### 2. Telemetry over that connection

In a recorded three-minute session, the official client sent these alongside its normal traffic:

| Time | Request | What it reports |
|---|---|---|
| +8.5 s | `analytics.v1.AnalyticsService/RecordCounters` | Usage counters |
| +9.0 s | `friend.v1.FriendService/BroadcastLocationChange` | Your location in the game (by its name; we didn't decode the payload). Sent again whenever it changes (4 times in 3 minutes) |
| +32 s | `analytics.v1.AnalyticsService/RecordEnabledMods` | Your enabled mods (1.7 KB) |
| +62 s | `heartbeat.v1.HeartbeatService/GameHeartbeat` | Your mods and the game mode you're playing |

The protocol also defines these, which the client calls when the matching thing happens:

| Service | Methods |
|---|---|
| `analytics.v1.AnalyticsService` | `recordPinnedServerInteraction` (servers you click) |
| `setting.v1.SettingService` | `updateClientSetting`: every setting you change, with what changed it (`initiator`) |
| `apollo.v1.ApolloService` | `trackFeatureAdoption` |
| `screenshot.v1.ScreenshotService` | `recordScreenshot` |
| `logupload.v1.LogUploadService` | `startLogUpload`, `completeLogUpload`: uploads your game logs |
| `performance.v1.PerformanceService` | `recordPerfSnapshot`, `startJfrUpload`, `completeJfrUpload` |

### 3. Remote profiling on request

Lunar's server can push `StartJfrProfilePush { duration }`. That makes the client record a Java Flight Recorder profile of your game and upload it through `startJfrUpload` and `completeJfrUpload`; the upload response supplies the file targets.

Independently of that, the client:
- **Keeps JFR recordings by default.** The launch API passes `-XX:FlightRecorderOptions=repository=jfr`, and every session leaves `.jfr` files in the game's `jfr` folder (about 250 KB in one of our sessions).
- **Contains a diagnostics collector** (`ThreadModuleDump` and its neighbours). It produces:
  - a list of **every thread and every DLL loaded into the game process**, with symbol names, using the Windows Toolhelp API;
  - native stack samples, including **"executable memory outside any module"**;
  - a JFR capture of socket reads and writes, file reads and writes, and thread activity;
  - `mods-enabled.txt`, `options.txt` and `optionsof.txt` (your settings), `feature-flags.txt`, `events.txt`, `f3-data.txt`, GPU and memory data, and build information.

  We haven't traced exactly which of these files go into the remote-profiling upload. The collector runs in modes named `PROFILER`, `THREAD_DUMP` and `PRE_COLLECT`.

### 4. Sentry error and performance reporting

Both jars start the Sentry SDK with a hardcoded project, `o923049.ingest.sentry.io/6029559`.
- **Traces:** `tracesSampleRate` is `1.0`, so every traced operation is reported, not a sample.
- **Breadcrumbs:** up to 100 recent client events are attached to each report.
- **Tags:** reports are tagged with your `canary_token`, launcher and Lunar versions, Minecraft version, modpack IDs, `ichor_modules`, `external_mods`, `gpu_vendor` and operating system.
- **Header:** the game also sends a `sentry-trace` header with its connections.

### 5. A separate analytics endpoint

The client bundles an HTTP analytics client for `https://analytics.lunarclientprod.com` that sends batches of events. With it blocked, the game log shows it retrying every 15 seconds: `[LC Analytics] Failed to send batch, retrying 1 events`.

### 6. Remote configuration, ads and tracking headers

At startup the game downloads `/game/metadata` from Lunar's API, sending your `X-Installation-Id` and `X-Overwolf-Muid` as request headers. The response remotely controls:
- the main-menu ad carousel (`blogPosts`);
- promoted servers (`pinnedServers`);
- banners such as "Link Your Discord" (`DiscordCTA`) and other promotion flags;
- which Sentry reporting handlers are switched on (`SentryReportHandler`, `SentryTraceSampling` and others).

### 7. A third-party music service

The client is configured for **Styngr** (`https://api.styngr.com/api`), an outside company's music API.

### 8. The official launcher

Lunar's own launcher sends its own handshake. Its `LauncherHandshake` fields include `session_id`, `uptime_since_start`, `started_at`, `elevated` (whether it runs as administrator), `analytics`, `optimized_ads`, `radio_info`, `safe_storage` and `settings`. Lunar's launch API also receives `installation_id`, OS details and a profile. The installation ID ties every launch to the same install.

## What BetterLC does about it

### In every mode

- **No official launcher.** BetterLC replaces it entirely, so none of section 8 is sent. Each launch uses a fresh random installation ID and profile, and reports the OS release as `unknown`.
- **Sentry is off.** BetterLC patches both jars to blank the Sentry project address. An empty address disables the Sentry SDK: no error reports, traces or breadcrumbs. The `sentry-trace` header carries no trace ID.
- **The analytics endpoint is dead.** BetterLC patches it to a closed local port, so batches fail on your PC and never leave it.
- **Styngr is off**, pointed at the same closed port.
- **No ads or tracking headers on the metadata request.** BetterLC routes Lunar's web API through a small proxy on your PC. It removes `X-Installation-Id`, `X-Overwolf-Muid` and `sentry-trace` from every request. In the metadata it empties the ad carousel and promoted servers, and switches off the Discord and promotion banners and all Sentry handlers. Everything else passes through unchanged.
- **No token left behind.** The game needs an account file containing your Minecraft access token; BetterLC writes it at launch and deletes it when the game closes. Your Lunar settings are kept between launches. Your Microsoft sign-in is stored encrypted with Windows' per-user protection (DPAPI).
- **Patches survive updates.** They're applied by pattern rather than to one exact build, so most Lunar updates keep working. If the override hook or the Sentry address can't be found in a new Lunar build, BetterLC refuses to launch rather than run it unpatched.

### Choose where the game's connection goes

| | **Offline** (default) | **BetterLC server** | **Lunar directly** |
|---|---|---|---|
| Handshake (section 1) | stays on your PC | scrubbed copy to Lunar, sent from the server | sent to Lunar as is |
| Telemetry, location, settings and log uploads (section 2) | answered on your PC | answered by the server, never forwarded | sent to Lunar |
| Remote profiling (section 3) | can't be triggered | can't be triggered (Lunar's pushes aren't passed on) | possible |
| Lunar's login service | stand-in on your PC | relayed through the server | direct |

- **Offline:** BetterLC runs a stand-in for Lunar's login and game services on your own PC (`127.0.0.1`). The game never opens Lunar's WebSocket services.
- **BetterLC server:** the game connects to a BetterLCSync server, BetterLC's self-hosted companion server. It answers everything itself and keeps one link to Lunar that carries only the cosmetics data needed to show other players. Before forwarding the handshake, the server removes `hwid`, `overwolf_muid`, `installed_mods`, `gl_extensions`, GPU memory and `settings`. It still forwards your account identity, `installation_id` (random per launch), the OS and CPU strings, `language`, `canary_token`, `ichor_modules` and `location`.
- **Lunar directly:** like the official client, minus everything in "In every mode" above.

## What BetterLC can't hide

- **Downloads and web APIs.** Game files, the version list and textures still come from Lunar's servers and CDN, so Lunar sees your IP when you launch. In every mode, some in-game features also call Lunar's web APIs directly: skins, the Hypixel and SkyBlock data cache (`thirdpartycache.lunarclientprod.com`) and SkyBlock price data.
- **Mojang.** Sign-in goes through Microsoft, Xbox and Mojang like any Minecraft launcher, and the game does Minecraft's normal session join. BetterLC uses Lunar's public Microsoft app ID, so Microsoft's sign-in page says "Lunar Client". Your tokens go only to Microsoft, Xbox and Mojang.
- **BetterLC server mode.**
  - Lunar sees your Minecraft account, the server's IP (not yours), and the UUIDs of players near you, because that's how other players' cosmetics are looked up.
  - Whoever runs the BetterLCSync server can see the traffic that passes through it.
- **Local recording.** The game still writes JFR recordings to its own folder. In Offline and BetterLC server modes they can't be uploaded.
- **Minecraft servers** you join see what they always see.

## Using BetterLC

Download `BetterLC.exe` from the [latest release](https://github.com/ehkogh/BetterLC/releases/latest) and run it. Pick a Minecraft version, loader and connection, then click **Launch**. The first launch shows a Microsoft one-time code; later launches reuse your saved sign-in, and **Sign out** removes it. You need a Minecraft: Java Edition account.

Each release comes with a `.sha256` file you can check the download against. BetterLC isn't code-signed yet, so Windows may warn you the first time you run it. Windows Defender's machine-learning scanner has also flagged builds before (`Bearfoos.A!ml`); that's a false positive.

Game files live in `%LOCALAPPDATA%\BetterLC\cache`, Lunar's settings in `cache\lunar`, and launcher choices in `%LOCALAPPDATA%\BetterLC\launcher.json`.

The same exe is a command-line tool:

```powershell
.\BetterLC.exe launch-version 1.8.9 lunar --mode offline
.\BetterLC.exe launch-version 26.1.2 fabric --mode betterlc --server https://sync.example.com
.\BetterLC.exe versions-json
.\BetterLC.exe sign-out
.\BetterLC.exe patch lunar.jar lunar-patched.jar
```

### Building it yourself

With the .NET 10 SDK:

```powershell
dotnet publish -c Release -o release
```

That gives you the same single `release\BetterLC.exe`.

## Checking our work

You don't have to take our word for any of this:

- **Read the jars.** After a launch they're in `%LOCALAPPDATA%\BetterLC\cache\original`. With a JDK installed, unzip one and run `javap -constants` on a class, e.g. `com.lunarclient.websocket.handshake.v1.Handshake`, to see its field names and numbers. Search the class files for strings like `ingest.sentry.io`, `analytics.lunarclientprod.com` or `ThreadModuleDump`.
- **Compare patched and original.** `BetterLC.exe patch <original.jar> <out.jar>` reports what it changed. Diff the patched jar against the original in `cache\original`.
- **Watch the traffic.** Point the game at a logging WebSocket proxy with `-DserviceOverrideAssetServer=ws://127.0.0.1:<port>`. The timings in section 2 came from exactly that.
- **Read the game log** at `%LOCALAPPDATA%\BetterLC\cache\multiver\logs\latest.log`. Lines like `[LC Analytics] Failed to send batch` show blocked telemetry failing locally.

## License

[MIT](LICENSE)
