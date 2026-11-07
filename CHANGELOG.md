# Changelog

## 3.1.2 (unreleased)

- **No more ghost accounts.** An extra Claude Code, Codex or Cursor account whose folder or login file (`.credentials.json`,
  `auth.json`, `state.vscdb`) is gone is no longer shown or read: with only one account left the card is a single-account
  one again, and if the missing account was the chosen one the main account is shown. The entry stays in the settings (it
  comes back with its folder, e.g. an external drive) and Settings › Agents › Accounts marks it as "Not found" so it can
  be deleted.
- **Main account not signed in.** When the default Claude Code, Codex or Cursor account has no session but an extra
  account does, the card shows the extra account (ring, plan badge, (i), ring-click renewal and tray tooltip all follow
  it) instead of a "sign in" tab; with one extra account it's a single-account card. A line under it, "Main account not
  signed in · Connect", starts the same sign-in as Settings › Agents. A loading or network error reading of the main
  account keeps the card as it was, so it doesn't switch layouts on every refresh.

## 3.1.1 — 2026-11-07

- **Resize without opening Settings.** When you hold down the ✕, besides the handle for moving it, the size grip (the
  same one as in "Edit widget") appears outside the panel: drag it to make the panel bigger or smaller, with a live %,
  a snap at 100 % and never beyond what fits on the screen; double click goes back to the automatic size. It's saved
  when you let go (`uiScale`). It works on all four edges and at any display scale.
- **The ✕ no longer turns into the handle so quickly.** You have to hold it for a second (previously 0.45 s); dragging
  it still turns it into the handle immediately. The handle and the grip stay for 6 idle seconds (previously 3), don't
  count down while you drag and, if the panel tucks away, the ✕ comes back right away.
- **Claude without a 5-hour window.** The usage API has stopped sending the 5-hour session for some accounts (only the
  weekly limit) and the ring showed "--". The weekly limit is now a valid reading: the ring shows it on "Session" and
  "Total", the card says "No session limit" with the weekly limit and its reset below, and it's saved in the cache and
  in the tray tooltip. If the session comes back, everything works as before; with neither of them, it's still an
  error.
- **Ultra.** Claude Max 20x accounts are labelled **Ultra** (its new name), both on the card and on each account's
  label; Max 5x is still Max. It's told apart by `rateLimitTier` in the credentials (read-only).
- **Fewer HTTP 429s.** After a 429 from Claude it waits 5, 10, 20 and then 30 minutes in a row (or longer if
  `Retry-After` asks for it), also for the extra accounts and the subscription (i); any successful response resets it.
  Codex waits 5 minutes after a 429 and 1 after a 5xx error. Retrying Claude from Settings doesn't fire another request
  if one is already in flight or the session is being renewed, and automatic renewal is no longer rescheduled for a
  token it has already given up on.
- If the atomic write of a data file fails, no stray `.tmp` file is left behind.
- **The Orb connection is removed.** Orb 3.8 no longer has the bridge with Open Control Edge, so the widget stops
  writing `oce.json` every 2 s in `%LOCALAPPDATA%\OrbPuente` and stops reading `orb.json`. The "Share data with Orb"
  setting (Settings → Agents; `"shareWithOrb"` from old settings is ignored) and the "Orb data" that filled in for a
  ring without its own reading are gone. Settings → More apps still shows Orb so you can open or download it. In
  Debug, `--orb-probe` is gone.

## 3.1.0 — 2026-10-14

- **Move the panel by holding down the ✕.** A ring fills up around it while you hold it; once it's full (or as soon as
  you drag) it becomes a filled handle (↔) and the panel follows the mouse to any edge of any screen. When you let go,
  the ring empties over 3 seconds and the ✕ comes back. A normal click still quits the app. In **Manual** the ✕ no
  longer stays a handle forever.
- **Edit widget** (Settings → Appearance): the ✕ becomes the handle, the gear a ✓ to finish from the panel itself, and a
  grip appears next to the panel: dragging it away or closer (in any direction) resizes it smoothly, with the
  percentage beside it; it snaps to 100 % and won't go beyond what fits on the screen; double click goes back to the
  automatic size. The ✓, "Done" or closing Settings saves the position and size and brings the ✕ back.
- Closing Settings also saves the size chosen with the slider even if its deferred save hadn't run yet.

## 3.0.0 — 2026-10-11

The definitive version. It includes what was prepared for 2.3.1, which was never released.

- **Wherever you want it.** Settings → Appearance → Position: the panel goes on the right, left, top or bottom edge (at
  the top and bottom, with the rings in a row and the card below or above), centred or in **Manual**: the panel's ✕
  button is then a handle (↔) for dragging it to any edge of any screen, and it stays where you drop it. Saved in
  `"edge"`, `"placement"` and `"offset"`.
- **Several screens.** You choose which screen it goes on (`"monitor"`, by its Windows name); if that screen is
  disconnected, it goes back to the primary one, and it repositions itself when the resolution, DPI or taskbar changes.
- **All your accounts, without hunting for them.** After each start, in the background and at low priority, it looks
  for your other Claude Code, Codex and Cursor sessions in your user folder and on every fixed drive (C:, D:…), without
  following links and skipping Windows, Program Files and the like; it recognises the same account across several
  folders (Codex by its `account_id`, Claude by its `accountUuid`, Cursor by its user id, always as a SHA-256
  fingerprint, never the value). Accounts you remove by hand don't come back (`"dismissedAccounts"`). In Settings →
  Agents → Accounts, "Search the whole PC" runs it on demand. The default folders respect `CODEX_HOME` and
  `CLAUDE_CONFIG_DIR`.
- **Adaptive card**: it widens as much as needed to fit the account labels. "prolite" is shown as "Pro Lite".
- **Account labels** next to the title ("Go", "Pro Lite" or the name you give it, e.g. "Personal" and "Work"): the
  filled one is the one on screen and clicking another switches account; with more than three they move to a second
  line. Every account, the main one included, has an optional name in Settings → Agents → Accounts.
- **Subscription (i)** on Claude, Codex and Cursor: "Your subscription will renew on …" for the account on screen
  (Codex: from its `id_token`; Cursor: the end of the cycle; Claude: `GET /api/oauth/profile`, only when the bubble is
  opened and at most every 6 h, computed from the sign-up day because Anthropic doesn't give the date).
- **"Open …" button** in the bottom-right corner of each AI card: the desktop app (Claude and Codex from the Microsoft
  Store, Cursor, ChatGPT) or its website; always without administrator rights.
- **Redesigned Settings**: "Open Control Edge" centred at the top, "Settings" in the sidebar and, at the bottom,
  @danielfinchdev (linked) and the version. **Contribute** (PayPal and feedback with screenshots) and **More apps** (Orb,
  with its latest version from GitHub, Download, View on GitHub and Open if you already have it).
- **Optional Custom view** (`"customView"`); **Game mode**, last in the PC view by default (also if you had the old order
  untouched) and with "On" / "Off"; the pin button is a pushpin that fills up; the view button is "Switch view" with a
  ↓↑ icon.
- The "Orb" badge above the rings and the Orb ring are removed; the bridge with Orb stays as it was.
- Fixes: the subscription bubble and the labels are no longer cut off when their text changes; when you delete an
  account, the card keeps showing the same one; renaming an account doesn't reset its reading; identical labels are
  numbered properly ("Pro", "Pro 2", "Pro 3"); an uninstalled Store app opens its website.
- **Security**: the start-up task no longer opens the program directly but `start.cmd` (in Program Files, with
  `conhost --headless` and `cmd /d`, no window), which clears the `DOTNET_*`, `COMPlus_*`, `CORECLR_*` and `COR_*`
  variables from the user's environment and pins `PATH` before opening the elevated widget: a *profiler* can no longer be
  loaded nor a diagnostic port opened from `HKCU\Environment`. The installed copy writes `start.cmd` and updates the task
  by itself at start-up. Session files (the main account's too) are read without following links, with a single hard
  link and only if they belong to your user; off the UI thread.
- **Careful account search**: at most once a day, only Cursor profiles whose path says so, and never sessions nobody has
  used in 60 days (copies or backups). A click on the Claude ring renews the account on screen.
- **More fixes**: on the taskbar edge or at the top/bottom, the automatic-mode strip waits a moment before opening the
  panel; dragging the panel is smoother; the Manual-mode handle never quits the app; a very long-lived token no longer
  breaks the renewal timer; long account folders are preserved.
- `--snapshot` checks the account search, the Claude profile, the Codex dates and the Orb Release, and adds screenshots
  of the panel on all four edges and in Manual.

## 2.3.0 — 2026-10-11

- **Connection with Orb.** The widget and Orb show each other their real data through two files in
  `%LOCALAPPDATA%\OrbPuente\`, with no network. The widget writes `oce.json` every 2 s (to `oce.json.tmp`, then renamed
  over it) with CPU load and temperature, the GPU, RAM in use, FPS and each AI's usage for the main account; whatever
  couldn't be read goes as `null`, and it never carries tokens, emails or paths. It reads `orb.json` (256 KB max, off the
  UI thread) and only accepts it with schema 1, less than 15 s old and a running `pid`.
- **"Orb" badge above the rings** while Orb is connected: the dot turns green when its assistant is working and, on
  hover, shows its tasks in progress, queued and awaiting approval ("no data" if Orb doesn't send them). It doesn't move
  anything on the panel.
- **Orb's usage where the widget's own is missing**: if the Claude, Codex or Cursor ring has no reading, it shows Orb's
  and the card says where it comes from ("Orb data · account · window"). Those readings aren't saved in the cache and
  aren't sent back to Orb.
- **Settings → Agents → Share data with Orb** (`"shareWithOrb"`, on by default): turning it off stops writing, deletes
  `oce.json` and stops reading `orb.json`. `oce.json` is also deleted when the widget closes.
- The installed (elevated) copy touches that folder, which belongs to the user, with a copy of the user's token without
  privileges, without the Administrators group and with medium integrity level, so that a link planted there can't
  redirect an elevated write.
- `--snapshot` checks the bridge (what counts as connected, what stays `null` and the exact shape of `oce.json`) and
  adds screenshots of the Orb badge and of a card with Orb data (219 PNGs). In Debug, `--orb-probe <file>` does three
  real exchanges and deletes `oce.json` when done.

## 2.2.0 — 2026-10-08

- **Panel views.** The panel shows a single view: **AI** (the AI rings), **PC** (FPS, CPU, GPU, Game mode and RAM) or
  **Custom** (any mix you like). A fourth round button switches view (AI → PC → Custom) with a fade, and the buttons now
  come in pairs: pin/hide and view at the top, settings and close at the bottom. In Settings → Appearance you choose the
  active view and, per view, which rings are shown and their order with ↑↓ (alphabetical by default; in PC, FPS first).
  The panel's scale is computed for the largest view (with whatever is available) so it doesn't jump when you switch
  view.
- **Panel background colour** (Settings → Appearance → Widget background): preset colours, `#RRGGBB` or R/G/B sliders;
  black by default. On light backgrounds, the text and rings switch to the dark variant automatically.
- **FPS ring** (the first in the PC view): the FPS of the foreground program, counted like PresentMon from ETW present
  events (Microsoft-Windows-DXGI 42/55, D3D9 1 and DxgKrnl with the event filter 166/168/171/184/252); per process the
  most frequent event type wins, and Electron/Chromium apps are counted through their GPU child process. The ETW session
  only exists while the ring is visible and is sampled once per second. It needs the elevated (installed) copy; without
  it, or if nothing presents, it shows the desktop composition FPS (`DwmGetCompositionTimingInfo`). The arc is FPS /
  monitor refresh rate; the card shows the process, the FPS bar, the frame time and the Hz.
- **Game mode** (ring with a gamepad icon; empty = off, filled = on). Disabled by default; you enable it on the new
  Settings → Game mode page, where you edit the programs it closes (by default: cloud sync, Phone Link, Teams and the
  Xbox Game Bar helpers), the services it stops (by default WSearch, SysMain and DiagTrack), the power plan (Balanced by
  default, High performance or Leave it), whether Xbox Game Bar captures are turned off (HKCU values) and whether the
  closed programs are opened again, as a normal user, when it's turned off. It never closes protected or system
  processes, the process of the foreground window, the widget itself or `claude.exe`, and there's a list of protected
  services (seclogon, PawnIO…). The previous state is saved to `gamemode.json`, in the data folder, before every change;
  it's restored on quitting and, if the PC shut down halfway, when the widget is opened again.
- **Several accounts per AI** (Claude, Codex and Cursor). In Settings → Agents → Accounts you add another account with
  its configuration folder (`CLAUDE_CONFIG_DIR` with `.credentials.json`, `CODEX_HOME` with `auth.json`, or Cursor's
  `--user-data-dir` with `User\globalStorage\state.vscdb`) and a name. There's still one ring per AI and the card has
  account tabs (Main · Work) to choose which one the ring shows; the choice is remembered. Renewing the session with a
  click on the Claude ring only works for the main account.
- **Cursor and OpenCode are no longer read with administrator rights.** Previously the elevated process opened
  `state.vscdb` and `opencode.db` with SQLite. Now the installed app starts its own executable as a normal user
  (`--read-database cursor|opencode <path|auto>`, with `__COMPAT_LAYER=RunAsInvoker`, hidden, inside a *job* that closes
  it and with a 20 s limit): it reads the file read-only and in defensive mode and writes a small JSON through a pipe.
  It costs a short start-up for each Cursor or OpenCode refresh (~0.1 s of CPU and ~30 MB for less than a second) and
  nothing between refreshes. Unelevated copies (Debug) read in-process.
- **SQLite without Microsoft.Data.Sqlite.** SQLitePCLRaw 3.0.5 is used with native SQLite **3.53.4** through its raw API
  (`UntrustedSqlite`), instead of 2.1.0's 3.53.3. The reference to `System.Security.Cryptography.ProtectedData` (it
  ships with .NET 10) and `Microsoft.Data.Sqlite` are also dropped from the published libraries.
- **Updates with a notice and the project's own signature.** Automatic checking is **on** by default (the
  `checkUpdates` key, which replaces `autoCheckUpdates`), at start-up and every 6 h. A new version puts a red dot on the
  settings button and a notice in Settings ("New version available · Download and install"); one click downloads,
  verifies and installs it, and the widget restarts. The Release workflow now signs the ZIP with the project's ECDSA
  P-256 key (the `UPDATE_SIGNING_KEY` secret) and publishes `OpenControlEdge-win-x64.zip.sig`: the app only installs a
  file whose signature matches the public key it carries, and whose executable says "Open Control Edge" and the
  announced version; a Release without a `.sig` is not installed. **Anyone on 2.1.0 must install 2.2.0 by hand once**:
  2.1.0 only accepts updates signed by SignPath. An `autoCheckUpdates: false` saved by 2.1 is ignored.
- **Feedback without an account and with screenshots.** Settings → Feedback → "Send" posts the message to FormSubmit
  (formsubmit.co), which forwards it by email to the author (their address stays behind a FormSubmit alias), with up to
  3 images (PNG or JPEG up to 5 MB: "Capture screen" takes a picture of the primary screen and "Add image…" picks a
  file) and an optional contact email so you can get a reply. "Open on GitHub" opens a pre-filled issue and copies the
  first screenshot to the clipboard so you can paste it.
- **System check on install.** The installation window starts with "Checking your system…": 64-bit Windows 10/11,
  administrator rights, the Secondary Logon service (re-enabled if it was disabled), PawnIO (installed automatically with
  `-install`) and detected AIs. Then, "Accept and install". If PawnIO fails, the installation carries on.
- **Hardening of the elevated copy** (2.2 audit): .NET no longer loads *startup hooks* (`DOTNET_STARTUP_HOOKS` from the
  user's environment doesn't reach the elevated process); DLLs requested by name are only searched for in System32 and
  in the app folder, never on `PATH`; when installing from the ZIP, the executable is locked from the moment it starts
  and exactly that one is installed; the Actions are pinned to specific commits.
- **Works on more PCs.** The GPU ring also appears with AMD graphics cards and with Intel ones that have a temperature
  sensor, not just NVIDIA. With User Account Control (UAC) turned off, Cursor, OpenCode and Claude renewal work (there's
  no unprivileged user to hand them to, so they run as the current one).
- **Settings and data.** New "Game mode" category (7 in total now). The About page shows the real major .NET version.
- **.NET 10.** SDK 10.0.401 (`global.json`) and `net10.0-windows`. The GitHub Actions workflows move to versions that
  run on Node 24 (`actions/checkout@v7`, `actions/setup-dotnet@v6`, `actions/upload-artifact@v7` and
  `signpath/github-action-submit-signing-request@v2`) and the Build workflow only runs on `main`.

## 2.1.0 — 2026-10-03

- **Outfit typeface** (400, 500 and 600; SIL OFL) throughout the app instead of Google Sans Flex, with tabular figures
  in the rings so they don't jiggle.
- **Less CPU at idle.** Measured on 2026-09-30 on this laptop with the unelevated Debug build, off-screen, 10 min after
  4 min of warm-up (CPU temperature isn't read without elevation), process CPU time per minute:

  | Panel | Before | After | UI | Render | Background (network, sensors) |
  |---|---|---|---|---|---|
  | Pinned and visible | 0.389 s | **0.081 s** | 0.192 → 0.009 | 0.100 → 0.006 | 0.097 → 0.065 |
  | Collapsed (automatic) | 0.253 s | **0.111 s** | 0.117 → 0.006 | 0 → 0 | 0.136 → 0.104 |

  Each reading animated rings and bars for 500–600 ms even if they were hidden or unchanged, and each frame repaints the
  whole layered window: now only what's on screen and moves by 3 % or more is animated. Pointer polling (every 150 ms)
  stops with the panel pinned and idle, and the mouse itself over the panel resumes it, and with the panel collapsed and
  the cursor still for 2 s, and raw mouse, pen or touch input resumes it. Usage refreshes reuse HTTPS connections
  instead of negotiating TLS every time (about 80 ms less CPU per refresh). The tray icon doesn't resend a text that
  hasn't changed. RAM doesn't go down: the managed heap takes 3 MB and the rest is WPF, native code and
  LibreHardwareMonitor.
- The Settings texts move to the `Strings.es.xaml` and `Strings.en.xaml` dictionaries.
- "Start with Windows" no longer gets stuck on "Checking…": the Task Scheduler query has a 5 s limit and, if it fails,
  says why.
- Claude with HTTP 429 or 5xx: the last good reading less than 15 min old keeps showing with "Data from X min ago" and
  it isn't retried for 5 min. Without a Claude Code session the CLI isn't run: the ring and "Connect" open
  `claude auth login` in a visible console, as a normal user.
- Short panel tooltips ("Free RAM"), Settings toggles without flicker or switching on their own, a closing animation for
  the panel (the reverse of the opening one) and Settings as a normal window, not always on top.
- Clear message if the Secondary Logon service (seclogon) is missing; the single-instance mutex can no longer be locked
  from another process; on uninstall, the data folder is handed back to the user; service messages translated into
  English.
- **In-app updates, fixed and hardened** (final audit before release):
  - The helper that applies the update started with its working directory inside the folder it then renames, which
    Windows doesn't allow: the update always failed and the widget stayed closed. Now it starts outside, waits for the
    previous app to close and, if anything fails, reopens the installed version.
  - The ZIP must contain exactly the executable and its 8 native libraries, not one file more. Previously only the
    executable's signature was checked, and any other DLL in the ZIP would have been installed and loaded as
    administrator.
  - The executable must be signed by SignPath Foundation (exact name, not "contains") and its version resource must say
    "Open Control Edge" and the announced version. Each library must be the one shipped with the installed version or
    be signed by its publisher (Microsoft, Xamarin or SignPath Foundation).
- **Local installation**: the executable has the SHA-256 hashes of its 8 native libraries compiled in (computed at build
  time from the files being published) and the installer only copies identical libraries.
- The Cursor and OpenCode SQLite databases are opened in defensive mode (read-only and query-only, `trusted_schema`
  off, cell checks, no memory mapping, regular files only). A provider hidden in Settings → Agents is never read, not
  even when that page is opened, which marks it as "Hidden".
- SignPath Foundation signing prepared: the executable's version resource says "Open Control Edge" and the version
  without a commit suffix, the Release checks that the tag matches the project version, passes that version to SignPath
  so it only signs that executable, and links the signing policy.
- **Native SQLite 3.53.3** (SQLitePCLRaw 2.1.13) instead of the 3.41 bundled with Microsoft.Data.Sqlite 8.0.11
  (SQLitePCLRaw 2.1.6), affected by CVE-2025-6965.
- Usage requests (Claude, Codex, Cursor, DeepSeek, OpenRouter) no longer follow redirects: the Cursor cookie and the API
  keys travel in headers that HttpClient keeps when redirecting, even to another domain.
- The installer checks the permissions of Program Files, the folder and every file **before** replacing the previous
  copy, and cancels if anyone other than Administrators, SYSTEM or TrustedInstaller can modify them (previously it only
  looked at three groups, the folder and the exe, and merely warned). Read-and-execute permissions no longer count as
  write access.
- Pasting an API key with spaces or line breaks no longer invalidates it. An unknown provider value in the settings file
  is ignored instead of preventing any change from being saved.
- The panel's X says "Quit Open Control Edge". "Free RAM" only reports the in-use memory freed and no longer mentions a
  cache it doesn't empty.
- Cleanup: dead code from the *standby* list purge, texts and comments that were no longer true ("2 / 6 minutes"
  cadence, `%LOCALAPPDATA%` paths for the installed copy), User-Agent with the real version, unified Authenticode
  verification, logic that relied on comparing Spanish strings, and a complete THIRD-PARTY-NOTICES (.NET and WPF
  runtime, HidSharp, Mono.Posix, SQLitePCLRaw and LibreHardwareMonitor's dependencies).

- **Protected elevated writes.** Settings, log, keys and cache of the installed widget are stored in
  `%ProgramData%\OpenControlEdge\<SID>`, with the product folder and the user folder hardened. The automatic copy of old
  settings during start-up is removed.
- **Verified installer and updates.** The installation copies the executable and the closed list of its 8 native
  libraries, nothing else from the folder; the update installer requires SHA-256 and a valid Authenticode signature from
  SignPath Foundation. Update and PawnIO downloads use fixed asset names; PawnIO requires the pinned signer
  `CN=namazso.eu`.
- The `--snapshot`, `--smoke-test` and `--test-update-fixture` arguments are only processed in Debug. `--portable`,
  `--no-elevate` and "Use without installing" are ignored or hidden when the process is elevated.
- Links, Explorer and logins are launched with the user's token. Feedback opens an empty public form and copies the
  draft so it can be reviewed and edited before sending.
- "Free RAM" is disabled by default, only trims processes in the interactive session and no longer purges the standby
  list.

Native behaviour: no PowerShell scripts or script-made tasks.

- **Native installation.** Opening the executable outside `C:\Program Files\OpenControlEdge` brings up the "Install
  Open Control Edge" window: with the single UAC prompt it copies the executable and its native libraries (temporary
  folder, SHA-256 against the original, swap with rollback, permission check; the Authenticode signature is only
  required for downloaded updates), protects the data folder (no links, administrators only, high integrity), keeps the
  old EdgeWidget folder, registers start-up with Windows in Task Scheduler (at sign-in, no delay, elevated, normal
  priority) and starts the installed copy. `--portable` only takes effect when the process is already running
  unelevated. Tray menu: "Start with Windows" and "Uninstall…". `tools\instalar.ps1` and `tools\claude-sesion.ps1` are
  removed.
- **Data on screen within 1–2 s of starting.** The last reading is saved in `cache.json` (no secrets) and drawn on
  opening. Published uncompressed and with ReadyToRun. Measured on 2026-09-29 on this laptop (i7-8750H, 8 GB, RAM at
  87 %), unelevated and with `cache.json`, time from process start to the first frame with data; "1st" is the first run
  after publishing (freshly written files, so not a true cold start):

  | Publish | 1st run | Warm (mean of 3) | First network reading | Memory at 15 s (WS / private) |
  |---|---|---|---|---|
  | Single compressed file (2.0) | 2.22 s* | 1.38 s | 1.92 s | 260 / 163 MB |
  | Uncompressed | 2.41 s | 1.13 s | 1.59 s | 149 / 94 MB |
  | **Uncompressed + ReadyToRun (chosen)** | **1.46 s** | **0.83 s** | **1.32 s** | **151 / 96 MB** |
  | Compressed + ReadyToRun | 1.84 s | 1.46 s | 1.94 s | 260 / 207 MB |

  \* no cache yet. ZIP: 60 MB → 67 MB.
- **Click on the Claude ring: renews the session without opening any window.** It runs the Claude Code CLI as a normal
  user (Explorer's token, `CreateProcessWithTokenW`), hidden, with the minimal request and a 60 s limit; the card shows
  "Renewing session…" and then the result. Automatic background renewal (`autoRenewClaude`) within the token's last
  minutes, at most once every 30 min.
- **Click on the CPU ring:** opens Settings › System › About, without administrator privileges.
- **RAM ring** with physical memory used, cached and committed. Click: "Free RAM" (trims the memory of accessible
  processes in the session, no standby purge), at most once a minute; enabled with `ramCleanup`.
- **Spend on the card**: Claude (usage credits, if above zero), Codex (credit balance if the account has credits) and
  Cursor (on-demand spend in USD). Only with real figures from the response.
- **Code signing in CI** with SignPath Foundation, conditional on the secret and variables existing; signing policy in
  the README.
- Fixed the encoding of `EdgeWindow.xaml.cs` (the PawnIO warning stopped being recognised).

## 2.0.0 — 2026-09-29

First version of Open Control Edge (formerly EdgeWidget 1.1).

- Renamed EdgeWidget to Open Control Edge, a WPF app for Windows 11 / .NET 8.
- Usage rings and cards for Claude, Codex, Cursor, OpenCode, DeepSeek and OpenRouter, with Session and Total views.
- CPU and NVIDIA GPU temperatures and load through LibreHardwareMonitor.
- **Essential requirement for reading CPU temperature: install PawnIO**, a signed driver that gives LibreHardwareMonitor access to the sensors. The installer detects whether it's missing and offers to install it through winget; it can also be downloaded from [pawnio.eu](https://pawnio.eu). Without PawnIO, the temperature rings show how to install it.
- DeepSeek and OpenRouter keys protected with DPAPI CurrentUser; local credentials read read-only.
- Pinned and Automatic modes, tray menu, persistent settings and `--snapshot` screenshot rendering.
- Installation from PowerShell into Program Files and start-up through a scheduled task.
- New interface: black tab with concave curves, rings with each AI's logo and a colour per provider (red above 85 %), a speech-bubble card and the Geist typeface.
- AI rings only appear if that AI is installed on the PC (adjustable with `providers`).
- Detection of each account's plan (Free / Go / Plus / Pro / Max…).
- Removed the manual Grok Bot ring, replaced by automatic Cursor.
- Security: native DLLs next to the executable (no longer extracted to a user folder), hardened data writes and a full audit.
- Self-contained Release build distributed as a ZIP of the application folder.
