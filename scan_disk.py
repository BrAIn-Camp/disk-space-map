"""Scan a drive and open an interactive map of what is using the space.

Usage:  python scan_disk.py [DRIVE] [--out FOLDER] [--no-open]
        e.g. python scan_disk.py D:

Writes one self-contained page, disk-map-<letter>.html, next to this script
(or next to DiskSpaceMap.exe when built with PyInstaller) and opens it.
"""
import argparse
import heapq
import json
import os
import pathlib
import shutil
import sys
import tempfile
import time
import traceback
import webbrowser

FILE_ATTRIBUTE_DIRECTORY = 0x10
FILE_ATTRIBUTE_REPARSE_POINT = 0x400
FILE_ATTRIBUTE_OFFLINE = 0x1000
FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000  # cloud-only placeholder (OneDrive etc.)
LINK_TAGS = {0xA0000003, 0xA000000C}  # junctions and symlinks: following them double counts

KEEP = 25 * 1024**2  # folders/files smaller than this fold into "(smaller items)"
TOP_FILES = 300
TEMPLATE = "disk-usage.html"
DATA_TAG = '<script src="disk-data.js"></script>'  # replaced by the scan results

stats = dict(files=0, dirs=0, cloud_files=0, cloud_bytes=0, links_skipped=0, bytes=0)
denied = []
exts = {}
biggest = []
last_report = time.time()


def clean(path):
    return path[4:] if path.startswith("\\\\?\\") else path


def report(path):
    global last_report
    now = time.time()
    if now - last_report >= 3:
        last_report = now
        print(f"{stats['dirs']:>9,} folders {stats['files']:>11,} files "
              f"{stats['bytes'] / 1024**3:8.1f} GB  {clean(path)[:90]}", flush=True)


def scan(path, name):
    stats["dirs"] += 1
    report(path)
    node = {"n": name, "s": 0, "f": 0}
    kids = []
    rest = rest_f = 0
    try:
        entries = os.scandir(path)
    except OSError:
        denied.append(path)
        node["d"] = 1
        return node
    try:
        with entries:
            for e in entries:
                try:
                    st = e.stat(follow_symlinks=False)
                except OSError:
                    continue
                attrs = st.st_file_attributes
                if attrs & FILE_ATTRIBUTE_DIRECTORY:
                    if attrs & FILE_ATTRIBUTE_REPARSE_POINT:
                        try:
                            tag = os.lstat(e.path).st_reparse_tag
                        except OSError:
                            tag = 0xA0000003
                        if tag in LINK_TAGS:
                            stats["links_skipped"] += 1
                            continue
                    child = scan(e.path, e.name)
                    node["s"] += child["s"]
                    node["f"] += child["f"]
                    if child["s"] >= KEEP:
                        kids.append(child)
                    else:
                        rest += child["s"]
                        rest_f += child["f"]
                    continue

                size = st.st_size
                if attrs & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_OFFLINE):
                    stats["cloud_files"] += 1
                    stats["cloud_bytes"] += size
                    continue
                stats["files"] += 1
                stats["bytes"] += size
                node["s"] += size
                node["f"] += 1
                ext = os.path.splitext(e.name)[1].lower() or "(no extension)"
                tally = exts.get(ext)
                if tally is None:
                    exts[ext] = [size, 1]
                else:
                    tally[0] += size
                    tally[1] += 1
                if size >= KEEP:
                    kids.append({"n": e.name, "s": size, "f": 1, "k": 1})
                else:
                    rest += size
                    rest_f += 1
                if len(biggest) < TOP_FILES:
                    heapq.heappush(biggest, (size, e.path))
                elif size > biggest[0][0]:
                    heapq.heapreplace(biggest, (size, e.path))
    except OSError:
        denied.append(path)

    if kids:
        if rest:
            kids.append({"n": "(smaller items)", "s": rest, "f": rest_f, "o": 1})
        kids.sort(key=lambda k: k["s"], reverse=True)
        node["c"] = kids
    return node


def summarise(root, drive, started):
    usage = shutil.disk_usage(drive + "\\")

    # Space Windows reports as used that we could not see (no admin rights:
    # restore points, other users' folders, system metadata). Hard links in
    # C:\Windows can push the scanned total above "used", hence the guard.
    hidden = usage.used - root["s"]
    if hidden > KEEP:
        root.setdefault("c", []).append(
            {"n": "(unreadable without admin: system / restore points)", "s": hidden, "f": 0, "u": 1})
        root["c"].sort(key=lambda k: k["s"], reverse=True)
        root["s"] += hidden

    return {
        "drive": drive,
        "scanned_at": time.strftime("%Y-%m-%d %H:%M"),
        "seconds": round(time.time() - started),
        "total": usage.total,
        "used": usage.used,
        "free": usage.free,
        "stats": stats,
        "denied_count": len(denied),
        "denied": [clean(p) for p in denied[:300]],
        "exts": sorted(([k, v[0], v[1]] for k, v in exts.items()), key=lambda x: -x[1])[:40],
        "biggest": [[clean(p), s] for s, p in sorted(biggest, reverse=True)],
        "tree": root,
    }


def write_page(data, template, out_dir):
    with open(template, encoding="utf-8") as f:
        page = f.read()
    if DATA_TAG not in page:
        raise RuntimeError(f"{template} is missing the {DATA_TAG} placeholder")
    # "<" is escaped so no file name can close the <script> tag early.
    payload = json.dumps(data, separators=(",", ":")).replace("<", "\\u003c")
    page = page.replace(DATA_TAG, "<script>window.DISK_DATA = " + payload + ";</script>", 1)
    name = f"disk-map-{data['drive'].rstrip(':')}.html"
    try:
        out = os.path.join(out_dir, name)
        with open(out, "w", encoding="utf-8") as f:
            f.write(page)
    except OSError:  # e.g. the exe sits in a read-only folder
        out = os.path.join(tempfile.gettempdir(), name)
        with open(out, "w", encoding="utf-8") as f:
            f.write(page)
    return out


def main(ask_for_drive):
    sys.setrecursionlimit(20000)
    frozen = getattr(sys, "frozen", False)
    here = os.path.dirname(sys.executable if frozen else os.path.abspath(__file__))
    bundle = getattr(sys, "_MEIPASS", here)  # where PyInstaller unpacks TEMPLATE
    system_drive = os.environ.get("SystemDrive", "C:")

    parser = argparse.ArgumentParser(description="Scan a drive and open a map of what is using the space.")
    parser.add_argument("drive", nargs="?", help=f"drive to scan, e.g. D: (default {system_drive})")
    parser.add_argument("--out", default=here, help="folder to save the map in (default: next to this program)")
    parser.add_argument("--no-open", action="store_true", help="don't open the map in a browser")
    args = parser.parse_args()

    drive = args.drive
    if drive is None and ask_for_drive:
        drive = input(f"Drive to scan (press Enter for {system_drive}): ")
    drive = (drive or "").strip().rstrip("\\/").upper() or system_drive
    if not drive.endswith(":"):
        drive += ":"
    if not os.path.isdir(drive + "\\"):
        print(f"Can't find drive {drive}")
        return 1

    started = time.time()
    print(f"Scanning {drive}\\ - this usually takes a minute or two...", flush=True)
    root = scan("\\\\?\\" + drive + "\\", drive)
    data = summarise(root, drive, started)
    out = write_page(data, os.path.join(bundle, TEMPLATE), args.out)
    print(f"Done in {data['seconds']}s: {stats['files']:,} files, {data['free'] / 1024**3:.1f} GB free "
          f"of {data['total'] / 1024**3:.1f} GB.\nSaved the map to {out}", flush=True)
    if not args.no_open:
        print("Opening it in your browser...")
        webbrowser.open(pathlib.Path(out).as_uri())
    return 0


if __name__ == "__main__":
    # A double-clicked exe runs in its own console window: ask which drive to
    # scan, and keep the window open at the end so the summary can be read.
    double_clicked = (getattr(sys, "frozen", False) and len(sys.argv) == 1
                      and sys.stdin is not None and sys.stdin.isatty())
    try:
        code = main(double_clicked)
    except Exception:
        traceback.print_exc()
        code = 1
    if double_clicked:
        input("\nPress Enter to close...")
    sys.exit(code)
