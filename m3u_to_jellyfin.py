#!/usr/bin/env python3
"""Create a Jellyfin audio playlist from an .m3u file using the Jellyfin API.

Usage:
  python3 m3u_to_jellyfin.py --url http://localhost:8096 --api-key YOUR_KEY \
      --m3u MyList.m3u --name "My List"

Create the API key in Jellyfin: Dashboard -> API Keys -> "+".
Add --dry-run first to see how many tracks match without creating anything.

Tracks are matched case-insensitively against the paths Jellyfin knows. If the full path
differs (different mount point), the folder/file tail (Artist/Album/Track.mp3) is used as a
fallback, so --map is usually not needed. If you do need it:

  --map "/run/media/me/music/=>/mnt/music/"      (can be given several times)
"""
import argparse
import json
import re
import sys
import unicodedata
import urllib.parse
import urllib.request

MAX_MISSING_SHOWN = 5
BATCH = 100


# ------------------------------------------------------------------ Jellyfin API

def api(args, method, path, params=None, body=None):
    url = args.url.rstrip("/") + path
    if params:
        url += "?" + urllib.parse.urlencode(params)
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    token = args.api_key.strip()
    req.add_header("Authorization",
                   f'MediaBrowser Client="m3u-import", Device="script", '
                   f'DeviceId="m3u-import", Version="1.1", Token="{token}"')
    req.add_header("X-Emby-Token", token)
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req) as resp:
        raw = resp.read()
    return json.loads(raw) if raw else None


def library_paths(args, user_id):
    """Return {path: item_id} for every audio item in the library."""
    mapping, start, page = {}, 0, 2000
    while True:
        res = api(args, "GET", "/Items", {
            "Recursive": "true",
            "IncludeItemTypes": "Audio",
            "Fields": "Path",
            "UserId": user_id,
            "StartIndex": start,
            "Limit": page,
        })
        items = res.get("Items", [])
        for it in items:
            if it.get("Path"):
                mapping[it["Path"]] = it["Id"]
        start += len(items)
        if not items or start >= res.get("TotalRecordCount", 0):
            return mapping


# --------------------------------------------------------------------- matching

def segments(path):
    """Lower-cased, unicode-normalized path segments; handles / and \\, drops . .. and drive letter."""
    p = unicodedata.normalize("NFC", path.replace("\\", "/")).lower()
    segs = [s for s in p.split("/") if s and s not in (".", "..")]
    if segs and re.fullmatch(r"[a-z]:", segs[0]):
        segs = segs[1:]
    return segs


def parse_maps(values):
    maps = []
    for v in values or []:
        if "=>" not in v:
            sys.exit(f'Bad --map "{v}": expected "old prefix=>new prefix"')
        old, new = (x.strip() for x in v.split("=>", 1))
        if old.lower().startswith("file://"):
            old = urllib.parse.unquote(old[7:])
        old = old.replace("\\", "/")
        # "a/=>b" must not glue folder and file name together
        if old.endswith("/") and new and not new.endswith("/"):
            new += "/"
        if old:
            maps.append((old, new))
    return maps


def apply_maps(path, maps):
    p = path.replace("\\", "/")
    for old, new in maps:
        if p.lower().startswith(old.lower()):
            return new + p[len(old):]
    return p


def variants(line):
    """Readings of an m3u line worth trying: file:// URL, percent-encoded, plain."""
    if line.lower().startswith("file://"):
        rest = line[7:]
        if not rest.startswith("/"):
            i = rest.find("/")
            rest = rest[i:] if i >= 0 else rest
        yield urllib.parse.unquote(rest)
        return
    yield line
    if "%" in line:
        decoded = urllib.parse.unquote(line)
        if decoded != line:
            yield decoded


def read_m3u(path):
    out = []
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#"):
                out.append(line)
    return out


def build_index(lib):
    by_full, by_name = {}, {}
    for path, item_id in lib.items():
        segs = segments(path)
        if not segs:
            continue
        by_full.setdefault("/".join(segs), item_id)
        by_name.setdefault(segs[-1], []).append((segs, item_id, path))
    return by_full, by_name


def common_suffix(a, b):
    n = 0
    while n < min(len(a), len(b)) and a[-1 - n] == b[-1 - n]:
        n += 1
    return n


def annotate(line, maps, index):
    """Hint for an unmatched line: where the library has a file with the same name."""
    _, by_name = index
    for variant in variants(line):
        segs = segments(apply_maps(variant, maps))
        if segs and segs[-1] in by_name:
            c = by_name[segs[-1]]
            more = f" (+{len(c) - 1} more)" if len(c) > 1 else ""
            return f"{line}\n      -> same file name in library: {c[0][2]}{more}"
    return f"{line}\n      -> file name not found in library"


def find_match(line, maps, index, allow_name_only=True):
    """Return (item_id, kind) with kind in exact/tail/name, or None."""
    by_full, by_name = index
    for variant in variants(line):
        segs = segments(apply_maps(variant, maps))
        if not segs:
            continue
        # 1. exact (case-insensitive) full path
        key = "/".join(segs)
        if key in by_full:
            return by_full[key], "exact"
        # 2. same file name; the longest agreeing path tail wins, ties are rejected
        candidates = by_name.get(segs[-1])
        if not candidates:
            continue
        best, best_id, tie = 0, None, False
        for c_segs, c_id, _path in candidates:
            n = common_suffix(segs, c_segs)
            if n > best:
                best, best_id, tie = n, c_id, False
            elif n == best and n > 0:
                tie = True
        required = min(2, len(segs))
        if not tie and best >= required and (best >= 2 or len(candidates) == 1):
            return best_id, "tail"
        # 3. file name occurs exactly once in the whole library
        if allow_name_only and len(candidates) == 1:
            return candidates[0][1], "name"
    return None


# ------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description="Create a Jellyfin audio playlist from an m3u file.")
    ap.add_argument("--url", required=True)
    ap.add_argument("--api-key", required=True)
    ap.add_argument("--m3u", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--user", help="Jellyfin user name (default: first user)")
    ap.add_argument("--map", action="append", metavar="OLD=>NEW",
                    help='optional path prefix mapping, e.g. "/old/music/=>/mnt/music/" (repeatable)')
    ap.add_argument("--no-name-only", action="store_true",
                    help="do not match tracks by file name alone (default: allowed when the name is unique)")
    ap.add_argument("--keep-duplicates", action="store_true",
                    help="keep repeated tracks (default: only the first occurrence is kept)")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    maps = parse_maps(args.map)

    users = api(args, "GET", "/Users")
    user = next((u for u in users if u["Name"] == args.user), None) if args.user else users[0]
    if not user:
        sys.exit("User not found")
    user_id = user["Id"]
    print(f"Using user: {user['Name']}")

    tracks = read_m3u(args.m3u)
    lib = library_paths(args, user_id)
    index = build_index(lib)
    print(f"m3u tracks: {len(tracks)}, audio items in Jellyfin: {len(lib)}")

    ids, missing, dupes = [], [], 0
    kinds = {"exact": 0, "tail": 0, "name": 0}
    seen = set()
    for line in tracks:
        hit = find_match(line, maps, index, not args.no_name_only)
        if hit is None:
            missing.append(annotate(line, maps, index))
            continue
        item_id, kind = hit
        kinds[kind] += 1
        if not args.keep_duplicates and item_id in seen:
            dupes += 1
            continue
        seen.add(item_id)
        ids.append(item_id)

    matched = len(tracks) - len(missing)
    print(f"matched: {matched} ({kinds['tail']} by folder/file name, {kinds['name']} by file name only), "
          f"duplicates skipped: {dupes}, not found: {len(missing)}")
    if missing:
        with open("missing_tracks.txt", "w", encoding="utf-8") as f:
            f.write("\n".join(missing) + "\n")
        print("Unmatched lines written to missing_tracks.txt")
        print("First few:", *missing[:MAX_MISSING_SHOWN], sep="\n  ")

    if args.dry_run or not ids:
        return

    pl = api(args, "POST", "/Playlists", body={
        "Name": args.name,
        "Ids": ids[:BATCH],
        "UserId": user_id,
        "MediaType": "Audio",
    })
    pl_id = pl["Id"]
    for i in range(BATCH, len(ids), BATCH):
        api(args, "POST", f"/Playlists/{pl_id}/Items", {
            "ids": ",".join(ids[i:i + BATCH]),
            "userId": user_id,
        })
        print(f"added {min(i + BATCH, len(ids))}/{len(ids)}")
    print(f"Done. Playlist '{args.name}' created with {len(ids)} tracks.")


if __name__ == "__main__":
    main()
