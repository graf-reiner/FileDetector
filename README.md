# FileDetector

A Windows tray utility that watches folders and tells you the moment something appears in them —
including everything that appeared while the app wasn't running.

## What it does

- Lives in the notification tray; no main window.
- Watches the **top level** of one or more folders (subfolder contents are deliberately not reported;
  a new subfolder itself is).
- Reports items that are **created, renamed, or deleted**.
- Raises a **toast notification** and pops up a **details window** listing what changed.
- Bursts are debounced: copying 200 files in produces **one** toast and **one** window, not 200.
- Remembers each folder's contents in a snapshot, so on startup it reports what changed while it was
  closed ("While you were away…").
- Keeps a searchable **history** of everything it has detected.
- Optionally **starts with Windows**.

## Build

Requires the .NET 10 SDK (`dotnet --version` → 10.x). If it is missing:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$env:TEMP\dotnet-install.ps1" -UseBasicParsing
& "$env:TEMP\dotnet-install.ps1" -Channel LTS -InstallDir "C:\Program Files\dotnet"
```

Then:

```powershell
.\build.ps1                 # build + test
.\build.ps1 -Publish        # -> publish\FileDetector.exe (self-contained, ~70 MB, runs anywhere)
.\build.ps1 -Publish -FrameworkDependent   # ~1 MB, needs the .NET 10 desktop runtime installed
```

## Using it

Run `FileDetector.exe`. On first launch it opens **Settings** because nothing is being watched yet.

Tray menu:

| Item | What it does |
|---|---|
| **Settings…** | Choose folders, ignore patterns, notification and timing options |
| **History…** | Everything detected so far, filterable by folder and change type |
| **Rescan now** | Compare every watched folder against its snapshot right now |
| **Pause watching** | Stop notifying. Resuming re-baselines silently, so churn during the pause is not replayed |
| **Open log folder** | Opens `%APPDATA%\FileDetector` |
| **Exit** | Quit |

### First run for a folder is quiet

Adding a folder that already contains 500 files records a baseline and notifies about nothing.
Only what changes *after* that is reported.

### Checking it on a machine with no desktop

```powershell
.\publish\FileDetector.exe --selftest-ui        # exit 0 = every window builds; details in app.log
.\publish\FileDetector.exe --screenshot .\shots # renders each window to a PNG
```

Useful on servers and in CI, where the tray and windows cannot be inspected by eye. Note that a
`DropDownList` combo box paints no text under `DrawToBitmap`, so the History window's filter boxes
look empty in a screenshot even when a value is selected.

Settings itself needs an interactive session: launched from a service or a non-interactive
session it logs a warning instead of opening (Windows forbids modal dialogs there). Watching
still works — configure it by editing `settings.json`.

## Where state lives

```
%APPDATA%\FileDetector\
  settings.json         configuration
  snapshots\*.json      one snapshot per watched folder (this is what powers offline catch-up)
  history.jsonl         append-only event log (rotates at 5 MB)
  logs\app.log          diagnostics (rotates at 2 MB)
```

"Start with Windows" is a value named `FileDetector` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## Design notes

- **Three sources of truth.** `FileSystemWatcher` gives immediate events but drops them under load
  and is unreliable on network shares, so a **periodic full rescan** (default 5 minutes) diffs the
  folder against its snapshot, and a watcher `Error` (buffer overflow) forces an immediate rescan.
- **Stability gate.** A newly created *file* is not announced until it stops growing and can be
  opened exclusively, so a large copy notifies once, when it finishes — not while it is half there.
  Folders are announced immediately.
- **Debounce.** Events are collected until the folder has been quiet for `DebounceSeconds`, with a
  10-second hard cap so a continuous copy still reports.
- **Coalescing.** Created-then-deleted reports nothing; created-then-renamed reports one item under
  its final name; renamed-then-deleted reports the original name as deleted.
- **Ignore patterns** (`*.tmp`, `*.crdownload`, `~$*`, …) are applied before anything else, and a
  rename *from* an ignored name (the `file.crdownload` → `file.zip` download pattern) counts as a
  new arrival.
- **Toasts with a real fallback.** Windows toasts are used when the platform is available; when it
  is not (some Server SKUs), the tray balloon is used instead. Which one is in play is recorded in
  `app.log` at startup.
- **Offline renames** cannot be distinguished from a delete plus an add — a snapshot holds no file
  identity — so the catch-up scan honestly reports them as both.
