# Disk Space Map

See what is filling up your Windows drive. A quick scan, then a clickable map
in your browser, a bit like WinDirStat.

- **Treemap**: bigger block = more space. Click a block to zoom in, Backspace or **Up** to go back.
- **Largest files**: your 100 biggest files, with a button to copy each path.
- **Space by file type**: which kinds of files (.mp4, .zip, ...) use the most.

## Download and run

1. Download **DiskSpaceMap.exe** from the [latest release](https://github.com/BrAIn-Camp/disk-space-map/releases/latest).
2. Double-click it. Press Enter to scan your Windows drive, or type another letter such as `D`.
3. After a minute or two the map opens in your browser. It's saved next to the exe as
   `disk-map-C.html`, so you can open it again later without rescanning.

Windows may say **"Windows protected your PC"** because the exe isn't code-signed.
Click **More info**, then **Run anyway**. If you'd rather not, use Python instead (below).

For a more complete scan, right-click the exe and choose **Run as administrator**. Without it,
protected system folders (like System Restore points) show up as one hatched block.

Works on Windows 10 and 11, including ARM laptops on Windows 11 (through Windows' built-in
x64 emulation). Drawing the map needs an internet connection, because the page loads
[d3](https://d3js.org) from a CDN.

## Run with Python instead

Needs [Python 3.8+](https://www.python.org/downloads/), nothing else.

```
python scan_disk.py        # your Windows drive
python scan_disk.py D:     # another drive
```

## Privacy

Everything stays on your computer. The map page contains your folder and file names,
so think before sharing it. Maps (`disk-map-*.html`) are in `.gitignore`.

## Build the exe yourself

```
pip install pyinstaller
pyinstaller --onefile --name DiskSpaceMap --add-data "disk-usage.html;." scan_disk.py
```

The exe lands in `dist\`. Pushing a version tag (`git tag v1.0.1`, then
`git push origin v1.0.1`) makes GitHub Actions build it and attach it to a new release.

## Notes

- Junctions and symlinks are skipped so nothing is counted twice.
- Online-only OneDrive files aren't counted, because they don't take up space on the disk.
