# WinXplorer

A fast, free, fully customizable file manager for Windows 10 and 11. It looks like the built-in File Explorer, follows your Windows light/dark theme and accent color, and adds what Explorer is missing.

☕ WinXplorer is free. If it helps you: https://buymeacoffee.com/jazeknom

## Install

Download `WinXplorer-Setup-<version>.exe` from [Releases](../../releases) and run it. It installs to
`Program Files\JKapps\WinXplorer` (asks for admin rights; you can choose "only for me" instead), includes .NET,
and can be removed in Settings › Apps like any other program. Your settings stay in `%LOCALAPPDATA%\JKapps\WinXplorer`.

The installer isn't code-signed yet, so Windows SmartScreen may say "Windows protected your PC" –
click *More info › Run anyway*.

**Use WinXplorer instead of File Explorer** (optional): Settings › General › *WinXplorer opens folders*.
Double-click on folders and drives, folder links and Win+E then open WinXplorer. The desktop, taskbar,
Control Panel and Recycle Bin stay with Windows. Switch it off – or uninstall – and Windows Explorer is the default again.

## Features (v0.1)

- **Tabs** (Ctrl+T / Ctrl+W / Ctrl+Tab, middle-click to close) – restored on start; drag to reorder, drag out of the window to open it in a new window; tabs shrink when many are open and the strip scrolls with the mouse wheel
- **Settings tab** (Ctrl+, or ⋯ › Settings): language, theme, 6 accent colors (or the Windows accent), startup and view options
- **Per-folder views**: every folder remembers its layout, icon size, sort order and grouping (View › *Use this view for all folders* makes it the default); **group by date** (Today, Yesterday, Earlier this week…) in the Sort menu
- **Space on a folder** shows what's inside without opening it; optional **preview on hover** (rest the mouse on a file or folder for a second; video/audio can play at once)
- **Frequent folders** block in the sidebar (optional), **folder sizes** in the size column (optional), empty disc drives and card readers are hidden
- **Your own context-menu commands** (e.g. `code "%1"`), also as icons in the quick row
- **HEIC / HEIF (iPhone, Samsung photos) and AVIF open on any PC** – decoded with the built-in libheif, no Microsoft codec needed; for WebP / camera RAW without Microsoft's free codecs the preview links straight to the right Store page
- Third-party components and their licenses: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)
- **Quick row in the context menu**: up to 8 icons on the first line (cut, copy, rename, delete, *new .php file*…), three sizes, chosen in Settings › Context menu
- **Customizable keyboard shortcuts**: Settings › Shortcuts & help – click a key, press a new combination; clashes are shown, reset per shortcut or all
- **Address bar commands**: type `cmd`, `ps`, `terminal` (or `code .`, `git …`, any program on PATH) to run it in the current folder
- **Customizable context menu**: WinXplorer's own short menu, editable in Settings (reorder, hide, add any item the Windows menu offers – 7-Zip, WinRAR, Send to…), or the full classic Windows menu; Shift+right-click always opens the full Windows menu. Compress to / extract ZIP built in
- **Languages**: English, Latviešu, Deutsch, Eesti, Lietuvių, Norsk bokmål, Polski, Русский, Svenska (follows Windows by default). Add your own: Settings › Add a translation, or drop `xx.json` into `%APPDATA%\WinXplorer\Languages` (template: `src/WinXplorer/Languages/en.json`)
- Offline network drives no longer hang a tab: after 15 s the pane says the location isn't responding
- **Split view**: left/right or top/bottom, each pane with its own history, address bar and search (F6 switches pane)
- **Customizable sidebar**: drag folders in to pin them, drag to reorder, right-click to rename/remove/hide single drives, separators, reset to default
- **Fast listing** (FindFirstFileEx with large fetch, virtualized lists, lazy icons/thumbnails on background threads); the last 60 folders are kept in memory and shown instantly, then checked on disk – changes are merged in place, so refreshes don't jump to the top
- **Real Explorer context menu** incl. all shell extensions (7-Zip, WinRAR, Open with, Send to…), in dark mode too
- **Preview & details pane**: images (incl. WebP/HEIC/AVIF/RAW via Windows codecs, SVG), video/audio, code with syntax highlighting, hex view for binaries, EXIF, folder size, SHA-256/MD5
- **Quick view** (Space on any file): text and code with syntax colors (select and copy; minified JSON is shown indented), video/audio with a volume slider, PDF, Word, Excel and other documents through the Windows previewers, Photoshop (.psd) and Illustrator (.ai) pictures without Adobe (Illustrator rendered sharp at any zoom through Windows' PDF engine), program details (version, company, x64/ARM, signature) for .exe/.dll, archive contents, hex view for the rest; ←/→ browse
- **Rename many files at once** (select several, F2): all names as editable text next to the current ones; column selection with Alt+drag or middle-button drag edits every line at once, find/replace (with regex), clashing names are pink and block the rename, Ctrl+Z undoes it
- **Sidebar apps**: pin programs, shortcuts or files (drag them in, shown without the shortcut arrow); drop files on a pinned app to open them with it
- **Low memory**: nothing of the search index is loaded while you browse – the first search on a drive maps its index file, a minute after the last search it's released again; drives are crawled in a short-lived background process
- **Archives** (zip, 7z, rar, tar, gz, jar, apk, epub…): double-click opens them like folders (read-only), drag files out, right-click › Extract; Compress to ZIP; the preview lists the contents as a numbered table with a folder tree; optional "Open in WinXplorer" entry (with a light/dark archive icon) in Windows' own menu (current user only)
- **Select with a rectangle**: drag in empty space, Ctrl adds to the selection
- **Image viewer** (Space or double-click opens, Space again closes): a window over the file list, wheel zoom, drag to pan, ←/→ or the wide click zones at the left/right edge to browse, filmstrip, rotate, crop (X: drag a rectangle, Enter – replaces the file, original to the Recycle Bin, or saves a copy), delete, F11 maximize
- **Breadcrumb address bar** with subfolder dropdowns, Tab completion, `%ENV%` variables
- **Search**: instant filter as you type, Enter = search all subfolders (wildcards `*.png` work)
- **Fast search index**: fixed drives are crawled once in the background (low priority, `C:\Windows` skipped by default) into a compact in-memory name table (~50 MB per million files), saved to `%LOCALAPPDATA%\WinXplorer` and kept current with a drive watcher. With it, typing in the search box searches all subfolders as you type – ~40 ms for 1.1 M names. Back returns to the results
- **This PC** shows drive cards with usage bars; hovering a file/folder shows location, size (folders are counted on demand), created and modified
- Sidebar groups: titled headers and plain separators
- Paste a screenshot from the clipboard straight into a folder as PNG
- Details / icon views (View menu, Ctrl+wheel to resize), sorting, hidden files, extensions, compact rows

## Build

Requires the .NET 8 SDK.

```
dotnet build src/WinXplorer
dotnet publish src/WinXplorer -c Release -o publish
```

Installer (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)): `.\build-installer.ps1` publishes a self-contained
build to `artifacts\app` and writes `artifacts\WinXplorer-Setup-<version>.exe`.

Settings are stored in `%APPDATA%\WinXplorer\settings.json`.

## Keyboard

Defaults – every one of them can be changed in Settings › Shortcuts & help.

| Key | Action |
| --- | --- |
| Enter / Backspace | Open / back |
| Alt+← → ↑ | Back / forward / up |
| Ctrl+L, Alt+D | Edit address |
| Ctrl+F, F3 | Search |
| F2 | Rename (several selected: rename all as text) |
| Del / Shift+Del | Recycle / delete permanently |
| Ctrl+C / X / V | Copy / cut / paste |
| Ctrl+Shift+C | Copy path |
| Ctrl+Shift+N | New folder |
| Alt+Enter | Properties |
| Space | Quick view / image viewer (open / close) |
| Ctrl+, | Settings |
| Ctrl+N | New window |
| F5 | Refresh |

## License

WinXplorer is open source under the [MIT License](LICENSE) – use it, change it and share it freely.
Third-party components keep their own licenses: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
