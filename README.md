# Disk Visualizer

A fast, read-only Windows app that scans every fixed and removable drive and shows where the space went as an interactive treemap.

![treemap of all drives](docs/screenshot.png)

## Features

- **All drives at once.** Box area is proportional to size; folders nest inside folders, files are colored by type. Free space and "Unreadable / other" blocks make each drive's area match its real capacity.
- **Fast, parallel scan.** No admin rights needed. Roughly 2.3 million files across 6 drives in about 8 seconds with a warm cache. Drives appear as they finish, and a slow or unready drive doesn't block the others.
- **Drill down.** Click to zoom into a folder; use the breadcrumbs, Backspace, or the mouse back button to go up. Right-click to open in Explorer or copy a path.
- **Search and filter.** Plain text, `*.mp4`-style wildcards, or `.iso` for an extension. Matches stay lit, everything else dims, and the toolbar shows the count and total size.
- **Largest tab.** The top 200 files (filterable by type) or folders in the current view.
- **Color by age.** Shades files by last-modified date, from under a week to over 3 years, so stale data stands out.
- **Space hogs.** Finds well-known space consumers (`Windows.old`, Windows Update downloads, shader caches, package caches, `node_modules`, WSL/Docker disks, the hibernation file and more). Each card says whether it's safe to clear and how to reclaim the space. The app itself never deletes anything.

## Running it

Requires Windows 10/11 and the [.NET 10 desktop runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet run -c Release
```

To build a single-file exe into `publish/`:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Add `--self-contained true` to run on machines without the .NET runtime (the exe is larger).

## Notes and limits

- Folders you don't have permission to read are counted under **Unreadable / other** (used space minus everything that could be counted).
- Files under 64 KB are grouped into one block per folder to keep memory down, so search can't find them individually.
- Sizes are logical file sizes, not size on disk. Hard-linked files (mostly in `C:\Windows\WinSxS`) are counted once per link.
- Cloud-only OneDrive placeholders count as 0 bytes, since they use no local space.

## Code layout

| Path | What it does |
|---|---|
| `src/Scanning/DriveScanner.cs` | Multi-threaded directory scan and size roll-up |
| `src/Treemap/TreemapControl.cs` | Squarified nested treemap layout, drawing, and hit testing |
| `src/Treemap/Theme.cs` | Colors for file types, age, and status badges |
| `src/Model/` | Tree nodes, file categories, search, the Largest query, and space-hog rules |
| `MainWindow.xaml(.cs)` | Toolbar, side panel, tooltips, and navigation |
