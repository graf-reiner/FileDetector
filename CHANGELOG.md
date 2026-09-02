# Changelog

## 1.0.2 — 2026-09-02

Release tooling only — the application itself is unchanged from 1.0.1.

- `tools\publish-release.ps1` no longer aborts when checking whether a release already exists.
  Windows PowerShell 5.1 wraps a native command's redirected stderr in an `ErrorRecord`, which
  `ErrorActionPreference = 'Stop'` escalates to a terminating error even on exit code 0; `gh` calls
  whose output is read or discarded now go through a helper that reads the real exit code.

## 1.0.1 — 2026-09-02

- Version tracking: `<VersionPrefix>` in the csproj is now the single source of truth, with
  `AssemblyVersion`/`FileVersion` derived from it and the running version read back at runtime.
- The build stamps the git commit into `InformationalVersion`, so a shipped exe reports e.g.
  `1.0.0+ba4899b` and can be traced to the source it came from.
- The version is shown in the Settings window title and logged at startup.
- `tools\bump-version.ps1` bumps the version, opens a CHANGELOG section, and optionally commits
  and tags. `tools\publish-release.ps1` pushes and publishes the GitHub release from those inputs.

## 1.0.0 — 2026-08-30

First release.

### Features

- **Tray application** with no main window. Menu: Settings, History, Rescan now, Pause watching,
  Open log folder, Exit. Single instance — a second launch surfaces the running one.
- **Watches the top level** of any number of folders. Subfolder *contents* are deliberately not
  reported; a new subfolder itself is.
- **Reports created, renamed and deleted items**, with modified files available as an option.
- **Toast notification** via the Windows notification platform, with an automatic **tray balloon
  fallback** where that platform is unavailable (notably Windows Server).
- **Popup details window** listing the batch, grouped by change type, with Open Folder / Open Item.
  A batch arriving while the window is open appends to it rather than stacking a second window.
- **Offline catch-up.** Each folder's contents are snapshotted to `%APPDATA%`, so changes made while
  the app was closed are reported on the next launch as "While you were away…".
- **Debounced batching** — a 200-file copy produces one notification, not two hundred.
- **Stability gate** — a new file is announced only once it stops growing and can be opened
  exclusively, so a large copy notifies once, at the end.
- **Periodic safety rescan** (default 5 minutes) plus recovery from `FileSystemWatcher` buffer
  overflow, because the watcher alone drops events and is unreliable on network shares.
- **Ignore patterns** (`*.tmp`, `*.crdownload`, `~$*`, …). A rename *from* an ignored name — the
  `file.crdownload` → `file.zip` download pattern — counts as a new arrival.
- **History window** over an append-only JSONL log, filterable by folder and change type.
- **Start with Windows** via the per-user `Run` key; no elevation required.
- **Unavailable folders** (unplugged drives, offline shares) are retried rather than reported as a
  mass deletion.

### Diagnostics

- `--silent` — suppress the startup notification (used by the Run key entry).
- `--selftest-ui` — build every window and exit; 0 means the UI is sound. For headless machines.
- `--screenshot <dir>` — render each window to a PNG.

### Known limitations

- A rename performed while the app was closed is reported as a delete plus an add. A snapshot holds
  no file identity, so the two are genuinely indistinguishable after the fact.
- Settings requires an interactive session; launched from a service it logs a warning instead of
  opening, and is configured by editing `settings.json`.
