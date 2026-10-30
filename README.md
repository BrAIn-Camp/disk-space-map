# Disk Space Map

See what is filling up your Windows drive. A quick scan, then a clickable map
in your browser, a bit like WinDirStat.

- **Treemap**: bigger block = more space. Click a block to zoom in, Backspace or **Up** to go back.
- **Largest files**: your 100 biggest files, with a button to copy each path.
- **Space by file type**: which kinds of files (.mp4, .zip, ...) use the most.

## Use it

You need [Python 3](https://www.python.org/downloads/) (3.8 or newer). Nothing else to install.

1. Download this repo (**Code → Download ZIP**) and unzip it.
2. Open a terminal in the folder and run:

   ```
   python scan_disk.py
   ```

   It scans your Windows drive (usually `C:`), which takes a minute or two, then opens the map.
   To scan another drive: `python scan_disk.py D:`

3. To refresh later, run the same command again and reload the page.

Run the terminal **as administrator** for a more complete scan. Without it, protected
system folders (like System Restore points) show up as one hatched block.

## Privacy

Everything stays on your computer. The scan writes your folder and file names to
`disk-data.js` next to the page. That file is in `.gitignore`, so don't share it.

## Notes

- Junctions and symlinks are skipped so nothing is counted twice.
- Online-only OneDrive files aren't counted, because they don't take up space on the disk.
- The page loads [d3](https://d3js.org) from a CDN, so it needs an internet connection.
