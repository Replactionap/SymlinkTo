# SymlinkTo

A small cross-platform utility that adds **"Create symlink in..."** and **"Delete symlink"** items to the context menu of your file manager. Select one or more files or folders, pick a destination folder in a native dialog, and the symlinks are created.

| OS      | File manager        | Where the items appear                                  |
|---------|---------------------|---------------------------------------------------------|
| Windows | Explorer            | Right click → *Show more options* (or `Shift+F10`)      |
| macOS   | Finder              | Right click → *Quick Actions* / *Services*              |
| Linux   | Nautilus (Files)    | Right click → *Scripts*                                 |

## Features

- Create symlinks for several files and folders at once; the folder picker is shown only once.
- Automatically avoids name collisions: `file`, `file (1)`, `file (2)`, ... (broken symlinks are taken into account too).
- Remembers the last chosen destination folder.
- "Delete symlink" removes **only symlinks** — never their targets or regular files.
- On Windows, if the user has no symlink privilege (no Developer Mode / not admin), the app re-launches itself through UAC to create the links.
- Native dialogs only: WinAPI/COM on Windows, `osascript` on macOS, `zenity` or `kdialog` on Linux.
- No admin rights needed to install the menu items on Windows (they are written to `HKCU`).

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer (to build).
- Linux: `zenity` (or `kdialog`) for dialogs, e.g. `sudo apt install zenity`.
- Windows: Developer Mode enabled **or** the ability to elevate via UAC.

## Build

```bash
# Windows
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Linux
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true

# macOS (Apple Silicon / Intel)
dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
dotnet publish -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true
```

The output binary is named after the project; the examples below assume `symlinkto` (`symlinkto.exe` on Windows).

> `install` must be run from the **published** binary. Running it via `dotnet run` is rejected, because the menu entries would point to the `dotnet` host instead of your program.

## Installation

```bash
symlinkto install
```

This registers the menu items for the current user:

- **Windows** — registry keys under `HKCU\Software\Classes\{*,Directory}\shell\`.
- **macOS** — Quick Action workflows in `~/Library/Services/`. If the items don't show up, enable them in *System Settings → Keyboard → Keyboard Shortcuts → Services → Files and Folders*.
- **Linux** — executable scripts in `~/.local/share/nautilus/scripts/`. Restart Files with `nautilus -q`.

Do **not** move or delete the executable after installing — the menu entries reference its current path. If you move it, run `install` again.

## Uninstallation

```bash
symlinkto uninstall
```

Removes the registry keys / workflows / scripts created by `install`. The last-used-folder setting is kept (see below).

## Usage

From the context menu: select files or folders → *Create symlink in...* → choose the destination folder.

From the command line:

```bash
symlinkto install                    # register menu items
symlinkto uninstall                  # remove menu items
symlinkto <path> [path ...]          # pick a folder and create symlinks to the given paths
symlinkto --remove <link> [link ...] # delete the given symlinks (non-symlinks are skipped)
```

`--create` is an internal mode used for the UAC-elevated re-launch on Windows and is not meant to be called manually.

## How it works

- **Batching on Windows.** Explorer starts one process per selected file. Each process drops its path into a temp queue directory, and one of them (the "leader", chosen via a named mutex) waits until no new files arrive, then handles the whole batch in a single run.
- **Elevation on Windows.** If creating a symlink fails with `ERROR_PRIVILEGE_NOT_HELD` (1314), the failed items are passed to an elevated copy of the program (`runas`) via `--create target link ...`.
- **Folder picker.** `IFileOpenDialog` (COM) on Windows, `osascript` on macOS, `zenity` with a fallback to `kdialog` on Linux.
- **Last folder.** Stored in `last-dir.txt` inside `symlinkto` under the user data directory:
  - Windows: `%LOCALAPPDATA%\symlinkto\`
  - macOS: `~/Library/Application Support/symlinkto/`
  - Linux: `$XDG_DATA_HOME/symlinkto/` or `~/.local/share/symlinkto/`

## Project layout

Everything lives in a single `Program.cs`:

| Type          | Responsibility                                                         |
|---------------|------------------------------------------------------------------------|
| `Program`     | Entry point and command-line dispatch                                  |
| `Linker`      | Creating / removing symlinks, unique naming, UAC elevation             |
| `WinBatch`    | Collecting several selected files into one batch on Windows            |
| `WinNative`   | Windows folder picker and message box via WinAPI/COM                   |
| `Ui`          | Cross-platform dialogs (folder picker, info and error messages)        |
| `Paths`       | Home and user data directories                                         |
| `Settings`    | Persisting the last used destination folder                            |
| `Installer`   | Registering / unregistering the context menu entries per OS            |

## Limitations

- Windows 11: the items are in the classic menu (*Show more options*), not in the new compact one.
- Linux: only Nautilus is supported out of the box.
- Symlink creation on Windows needs Developer Mode or administrator rights.