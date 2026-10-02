#!/usr/bin/env python3
"""Build a release of the plugin and register it in manifest.json.

Usage:
  python3 release.py 1.1.0.0 "Short changelog"

What it does:
  1. sets the version in meta.json and the .csproj
  2. runs `dotnet publish`
  3. creates dist/m3uimport_<version>.zip (DLL + meta.json at the top level)
  4. adds/updates the version entry in manifest.json (with the MD5 checksum Jellyfin verifies)

Then follow the printed steps: upload the zip as a GitHub release asset, commit and push manifest.json.
"""
import datetime
import hashlib
import json
import pathlib
import re
import shutil
import subprocess
import sys
import zipfile

REPO = "dodog/jellyfin-plugin-m3uimport"
ROOT = pathlib.Path(__file__).resolve().parent
PLUGIN = ROOT / "Jellyfin.Plugin.M3UImport"
MANIFEST = ROOT / "manifest.json"
DLL = "Jellyfin.Plugin.M3UImport.dll"


def main():
    if len(sys.argv) < 2 or not re.fullmatch(r"\d+\.\d+\.\d+\.\d+", sys.argv[1]):
        sys.exit('usage: release.py <version like 1.1.0.0> ["changelog"]')
    version = sys.argv[1]
    changelog = sys.argv[2] if len(sys.argv) > 2 else f"Release {version}"
    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    # 1. versions
    meta_path = PLUGIN / "meta.json"
    meta = json.loads(meta_path.read_text(encoding="utf-8"))
    meta["version"] = version
    meta["timestamp"] = now
    meta_path.write_text(json.dumps(meta, indent=2) + "\n", encoding="utf-8")

    csproj = PLUGIN / "Jellyfin.Plugin.M3UImport.csproj"
    text = csproj.read_text(encoding="utf-8")
    for tag in ("AssemblyVersion", "FileVersion"):
        text = re.sub(rf"(<{tag}>)[^<]*(</{tag}>)",
                      lambda m: f"{m.group(1)}{version}{m.group(2)}", text)
    csproj.write_text(text, encoding="utf-8")

    # 2. build
    out = PLUGIN / "out"
    shutil.rmtree(out, ignore_errors=True)
    subprocess.run(["dotnet", "publish", "-c", "Release", "-o", str(out)], cwd=PLUGIN, check=True)

    # 3. zip: DLL + meta.json at the top level
    dist = ROOT / "dist"
    dist.mkdir(exist_ok=True)
    zip_name = f"m3uimport_{version}.zip"
    zip_path = dist / zip_name
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(out / DLL, DLL)
        z.write(meta_path, "meta.json")
    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest().upper()

    # 4. manifest
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    entry = manifest[0]
    for key in ("guid", "name", "description", "overview", "owner", "category"):
        entry[key] = meta[key]
    entry["versions"] = [v for v in entry.get("versions", []) if v["version"] != version]
    entry["versions"].insert(0, {
        "version": version,
        "changelog": changelog,
        "targetAbi": meta["targetAbi"],
        "sourceUrl": f"https://github.com/{REPO}/releases/download/v{version}/{zip_name}",
        "checksum": checksum,
        "timestamp": now,
    })
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    print(f"\nBuilt {zip_path}\nMD5: {checksum}\n")
    print("Next steps:")
    print(f"  1. Create the GitHub release v{version} and upload {zip_path}:")
    print(f'       gh release create v{version} "{zip_path}" --title v{version} --notes "{changelog}"')
    print("     (or on the website: Releases -> Draft a new release -> tag v%s -> attach the zip)" % version)
    print("  2. Commit and push (after the release exists):")
    print(f'       git add -A && git commit -m "Release {version}" && git push')
    print("  3. In Jellyfin: Dashboard -> Plugins -> Repositories -> add")
    print(f"       https://raw.githubusercontent.com/{REPO}/main/manifest.json")


if __name__ == "__main__":
    main()
