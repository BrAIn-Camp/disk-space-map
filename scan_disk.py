"""Scan a drive and write disk-data.js, then open disk-usage.html to explore it.

Usage:  python scan_disk.py [DRIVE] [OUT_DIR]
        e.g. python scan_disk.py D:
"""
import heapq
import json
import os
import pathlib
import shutil
import sys
import time
import webbrowser

FILE_ATTRIBUTE_DIRECTORY = 0x10
FILE_ATTRIBUTE_REPARSE_POINT = 0x400
FILE_ATTRIBUTE_OFFLINE = 0x1000
FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000  # cloud-only placeholder (OneDrive etc.)
LINK_TAGS = {0xA0000003, 0xA000000C}  # junctions and symlinks: following them double counts

KEEP = 25 * 1024**2  # folders/files smaller than this fold into "(smaller items)"
TOP_FILES = 300

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


def main():
    sys.setrecursionlimit(20000)
    drive = (sys.argv[1] if len(sys.argv) > 1 else os.environ.get("SystemDrive", "C:")).rstrip("\\/")
    if not drive.endswith(":"):
        drive += ":"
    here = os.path.dirname(os.path.abspath(__file__))
    out_dir = sys.argv[2] if len(sys.argv) > 2 else here
    started = time.time()
    print(f"Scanning {drive}\\ ...", flush=True)
    root = scan("\\\\?\\" + drive + "\\", drive)
    usage = shutil.disk_usage(drive + "\\")

    # Space Windows reports as used but we could not see (no admin rights:
    # restore points, other users' folders, system metadata). Hard links in
    # C:\Windows can push the scanned total above "used", hence the guard.
    hidden = usage.used - root["s"]
    if hidden > KEEP:
        root.setdefault("c", []).append(
            {"n": "(unreadable without admin: system / restore points)", "s": hidden, "f": 0, "u": 1})
        root["c"].sort(key=lambda k: k["s"], reverse=True)
        root["s"] += hidden

    data = {
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
    out = os.path.join(out_dir, "disk-data.js")
    with open(out, "w", encoding="utf-8") as f:
        f.write("window.DISK_DATA = ")
        json.dump(data, f, separators=(",", ":"))
        f.write(";\n")
    print(f"Done in {data['seconds']}s: {stats['files']:,} files, "
          f"{stats['bytes'] / 1024**3:.1f} GB readable, {len(denied):,} folders denied -> {out}", flush=True)
    page = os.path.join(out_dir, "disk-usage.html")
    if os.path.exists(page):
        webbrowser.open(pathlib.Path(page).as_uri())


if __name__ == "__main__":
    main()
