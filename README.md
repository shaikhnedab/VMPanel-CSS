# VMPanel — CounterStrikeSharp VIP Manager

A CounterStrikeSharp port of [Summer-16's SourceMod VMPanel](https://github.com/Summer-16/CSGO-VMPanel)
for Counter-Strike 2: MySQL-backed VIP management with a server-driven **Panorama HUD menu**
(`css_vip`) and a full in-game **admin menu** (`css_vipadmin`), powered by
[PanoramaManager](https://github.com/Next-il/PanoramaManager) on top of the official
`CCSCustomHudLayout` API.

Current version: **3.5.0**

## Features

- MySQL-backed VIPs (one VIP table per server, plus `tbl_servers` registry and `tbl_audit_logs`)
- SourceMod flag → CounterStrikeSharp permission mapping (`0:a` style, with immunity)
- Panorama HUD menu with clickable buttons, toasts, and per-player content
- Admin menu fully in-HUD: refresh, add VIP (pick online player → duration buttons → confirm),
  paged browse/remove list, remove by SteamID — chat commands remain as fallback
- VIP expiry alerts (chat) + connect toast
- Full localization: every player-facing string in `lang/*.json`, per-player locale via `css_lang`
- Works while dead / free-spectating; graceful chat fallback when spectating another player in-eye
  (the engine draws the *observed* slot there, so no plugin can render a personal HUD)

## Requirements

| What | Version | Notes |
|------|---------|-------|
| CS2 dedicated server | latest | SteamCMD `app_update 730` |
| MetaMod:Source | 2.x (CS2) | [sourcemm.net](https://www.sourcemm.net/downloads.php?branch=dev) |
| CounterStrikeSharp | 1.0.374+ | [releases](https://github.com/roflmuffin/CounterStrikeSharp/releases) — ships the `CCSCustomHudLayout` API |
| MySQL / MariaDB | 5.7+ / 10.3+ | Tables auto-create on first boot |
| .NET 10 SDK | 10.x | **build machine only** (to compile the plugin) |
| PanoramaManager | 0.4.1 | NuGet — pulled automatically on restore, see below |

## Third-party code note

This repo distributes **only its own code and assets**. `PanoramaManager.dll` and
`MySqlConnector.dll` belong to their respective authors and are **not** committed here —
they arrive via NuGet on `dotnet restore` (the `PackageReference`s in `VMPanel.csproj`
stay as-is; do not remove them) and the build copies them next to the plugin automatically.
If you publish a release zip, either exclude third-party DLLs with a pointer to NuGet,
or comply with each package's license.

## Server setup (dedicated)

1. Install/Update the server: `steamcmd +force_install_dir ./cs2 +login anonymous +app_update 730 +quit`.
2. Install **MetaMod:Source** (extract into `game/csgo/`), then **CounterStrikeSharp**
   (extract into `game/csgo/` so `addons/counterstrikesharp/` exists).
3. For a public community server, set a GSLT: `sv_setsteamaccount <token>` (from
   [steamcommunity.com/dev/managegameservers](https://steamcommunity.com/dev/managegameservers)).
4. Pterodactyl/hosts: the above usually comes pre-installed in the CS2 egg — you only need
   the `game/csgo/` side below.

## Plugin install (build from source)

There are no prebuilt binaries — build it (one command pulls all NuGet deps, including
PanoramaManager):

```powershell
cd VMPanel
dotnet restore
dotnet build -c Release
```

The `CopyPluginFiles` target assembles the runnable plugin in `VMPanel/`:

```text
VMPanel/
  VMPanel.dll            <- our code
  VMPanel.pdb
  MySqlConnector.dll     <- from NuGet (MySqlConnector 2.4.0)
  PanoramaManager.dll    <- from NuGet (PanoramaManager 0.4.1)
  lang/en.json           <- our strings (add fr.json etc. beside it)
```

Copy that folder to the server:

```text
game/csgo/addons/counterstrikesharp/plugins/VMPanel/
```

Restart the server once — the plugin generates its config, then restart again after editing it:

```text
game/csgo/addons/counterstrikesharp/configs/plugins/VMPanel/VMPanel.json
```

## MySQL setup

```sql
CREATE DATABASE vmpanel CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'vmpanel'@'%' IDENTIFIED BY 'strong-password-here';
GRANT ALL PRIVILEGES ON vmpanel.* TO 'vmpanel'@'%';
FLUSH PRIVILEGES;
```

Tables (`<ServerTable>`, `tbl_servers`, `tbl_audit_logs`) are created with
`IF NOT EXISTS` on boot; server registration is idempotent (`ON DUPLICATE KEY UPDATE`).
Each game server should use its **own** `ServerTable` value (e.g. `sv_retakes`, `sv_dm`).

## Configuration (`VMPanel.json`)

| Key | Default | What it does |
|-----|---------|--------------|
| `DatabaseConnectionString` | `Server=127.0.0.1;…` | **Required.** MySQL connection string |
| `ServerTable` | `sv_table` | **Required.** Per-server VIP table (`A-Za-z0-9_` only) |
| `PanelUrl` | `https://vmpanel.example` | Renew/purchase link shown to players |
| `DefaultVipFlag` | `0:a` | SourceMod-style flag for new VIPs (`immunity:flags`) |
| `VipDurationOptions` | `[30, 60, 90, 365]` | Duration buttons in Add-VIP (max 4, 1–36500, live — no recompile) |
| `ReturnHomeDelaySeconds` | `2.5` | Result screen → admin home delay |
| `AlertEnabled` | `true` | Expiry warnings on connect |
| `AlertDelaySeconds` | `20` | Delay after connect before the warning |
| `AlertDays` | `2` | Warn when this many days (or fewer) remain |
| `AlertDisplayType` | `1` | `1` = 3-line detail, `2` = short |
| `VipStatusPermission` | `@css/reservation` | Who may run `css_vipstatus` when *not* a VIP (VIPs always see their own) |
| `AdminMenuPermission` | `@css/root` | Who may open the admin menu / see the Admin row |
| `RefreshIntervalMinutes` | `5` | DB re-read interval |
| `AutoRegisterServer` | `true` | Insert this server into `tbl_servers` |
| `ServerName` / `ServerIp` / `ServerPort` | auto | Override registration values (blank = `hostname` / `hostip` / `hostport`) |
| `UseLocalTime` | `false` | `false` = UTC expiry times, `true` = server local |
| `ToastEnabled` / `ToastDurationSeconds` | `true` / `6` | Connect toast for VIPs |

## Panorama HUD files (workshop addon)

The HUD layouts **must exist on every client** — the server only sends strings/class toggles,
it cannot push files. This project's layouts ship as a workshop item:

**https://steamcommunity.com/sharedfiles/filedetails/?id=3801047068**

### Subscribing (players — do this)

1. Open the link above in your browser (logged into the Steam account you play CS2 with).
2. Click the green **Subscribe** button. Steam queues the download.
3. Open/start Steam and let the download finish (Steam → Downloads, or it completes silently).
4. **Fully restart CS2** — layouts load once per game session; reconnecting to a server is
   not enough after a fresh subscribe or an addon update.
5. Join the server and type `css_vip` in console. If the menu opens empty, the files didn't
   arrive: check the subscription is still active on the item page and restart the game again.

To unsubscribe later, return to the same page and click **Unsubscribe**.

**Loose-file alternative** (testing only): copy the compiled `*.vxml_c` / `*.vcss_c` into the
client's `game/csgo/panorama/layout/custom_game/` + `styles/custom_game/`. **Warning:** CS2
updates/verify routinely wipe loose files — the workshop subscription survives them.

### For server operators

Nothing is required server-side for rendering (the files do nothing there); keep a copy only
as distribution backup. Tell your players to subscribe to the item above.

### Compiling the layouts yourself

Sources live in `workshop/panorama/` (`.xml` layouts, `.css` styles — the compiled names the
plugin asks for are `vmpanel_{menu,admin,toast}.vxml_c` + `vmpanel_menu.vcss_c`).

1. Optional but recommended — validate first (catches silent Panorama rejections):
   ```powershell
   python <PanoramaHUD-Skills>/cs2-panorama-hud/scripts/validate.py workshop
   ```
   Uses [PanoramaHUD-Skills](https://github.com/Next-il/PanoramaHUD-Skills) (`SKILL.md`,
   the `libpanorama.so` CSS vocabulary, `preview.py`). Read that skill before hand-editing
   CSS — Panorama silently drops web-isms (`rgba()`, `display: flex`, `contain`, …).
2. Stage into a CS2 addon content dir, keeping the paths:
   ```text
   content/csgo_addons/<addon>/panorama/layout/custom_game/*.xml
   content/csgo_addons/<addon>/panorama/styles/custom_game/*.css
   ```
3. Compile each file with the shipped compiler (output lands under the matching
   `game/csgo_addons/<addon>/panorama/…` as `*.vxml_c` / `*.vcss_c`):
   ```powershell
   & "<cs2>\game\bin\win64\resourcecompiler.exe" "<full path to file>"
   ```
   Layout sources are `.xml` (Valve's own example: `content/csgo_addons/cs_script_demo/
   panorama/layout/custom_game/welcome.xml` → `welcome.vxml_c`). The root panel of a
   layout must **not** carry an `id` — the compiler rejects it.
4. Publish/update the workshop item from that addon (CS2 Workshop Tools), or distribute
   the compiled files as loose client files (see warning above).

## Client install (players)

1. Subscribe to the workshop item above and let Steam finish the download, **then fully
   restart CS2** (layouts load per session — reconnecting is not enough after an update).
2. Join the server. `css_vip` opens the menu; if the menu opens empty, the client files
   are stale/missing — re-check the subscription and restart the game.

## Commands

| Command | Permission | Description |
|---------|------------|-------------|
| `css_vip` / `css_vipmenu` | — | Open HUD menu (always ensures open) |
| `css_vipclose` | — | Close HUD menu (also the escape hatch while spectating) |
| `css_viphelp` | — | Command overview |
| `css_vipstatus` | VIP or `VipStatusPermission` | Your VIP status |
| `css_viplist` | `@css/generic` | Connected VIPs |
| `css_online` | `@css/generic` | Online players + SteamIDs (chat admin work) |
| `css_viprefresh` | `@css/generic` | Reload VIP data from DB |
| `css_vipadmin` | `AdminMenuPermission` | Panorama admin menu (chat fallback while spectating) |
| `css_addvip <steamid> <days> <name>` | `@css/root` | Add/update VIP (1–36500 days) |
| `css_delvip <steamid>` | `@css/root` | Remove VIP |
| `css_hud_debug` | `@css/root` | HUD/handles debug dump (server log) |
| `css_panorama_diag` | (PanoramaManager) | Click channel, entities, per-slot state |

In-game chat shortcuts work too (`!vip`, `!vipadmin`, …). Admin menu flows: **Add VIP** =
pick online player → duration buttons → auto name from server → Confirm (manual-SteamID
button covers offline players); **Browse** = paged VIP list, tap twice to remove with
inline confirm; result screens return home automatically.

## Localization

All player-facing text lives in `plugins/VMPanel/lang/<locale>.json` (`en.json` ships with
152 keys: the original SourceMod phrases, typos fixed, plus every new HUD/chat string).
Colors use CounterStrikeSharp `{named}` codes; values take standard `{0}` format args.
Players choose their language with the built-in `css_lang` / `!lang` command — menus,
prompts and replies follow each viewer's locale (`Localizer.ForPlayer`), console output
uses the server language. To add a language, copy `en.json` → e.g. `fr.json` and translate
the values (keep the keys and `{0}` placeholders). Two documented exceptions stay English:
`[ConsoleCommand]` descriptions (attribute strings must be compile-time constants) and
server logs.

## Spectating / dead players

- Alive, dead-but-free-looking, roaming: full HUD.
- In-eye / chase of another player: the engine draws the *observed* slot, so the HUD is
  refused (silently, by PanoramaManager) and the same content is served over chat instead.
  `css_vipclose` always releases a stuck cursor.

## Troubleshooting

| Symptom | Cause → fix |
|---------|-------------|
| Client console `[custom_hud] Unable to find panel 'rowN'` | Layout/DLL version mismatch — update the client files *and* restart the game |
| Menu opens empty, fills after a click | Old plugin build — update server DLL and restart the server |
| `Unhandled button id: <name>` in server log | Client layout older than server plugin (or vice versa) — sync both |
| Files vanish from `game/csgo/panorama/` | Normal CS2 update/verify behavior — use the workshop subscription |
| `Database is not ready` / MySQL errors on boot | Connection string / grants — check `VMPanel.json` and MySQL user |
| No per-player language | Player hasn't set `css_lang`; missing locale falls back to server language |

## Credits

- Original SourceMod VMPanel: [Summer-16](https://github.com/Summer-16/CSGO-VMPanel)
- HUD engine: [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (`CCSCustomHudLayout`)
- Menu library: [PanoramaManager](https://github.com/Next-il/PanoramaManager) (NuGet — not redistributed here)
- Layout authoring: [PanoramaHUD-Skills](https://github.com/Next-il/PanoramaHUD-Skills)
