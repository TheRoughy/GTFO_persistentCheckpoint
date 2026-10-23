"""Build and package a Thunderstore release using only Python's standard library."""

import argparse
import binascii
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import zipfile


PROJECT_ROOT = Path(__file__).absolute().parent
ASSEMBLY = "TheRoughy.PersistentCheckpoint.dll"
SCREENSHOTS = (
    "resume-checkpoint.png",
    "create-checkpoint.png",
)
VERSION_PATTERN = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"


class ReleaseError(Exception):
    pass


def read_version(root):
    source = root / "GTFO_PersistentCheckpoint/Core/PersistentCheckpointPlugin.cs"
    match = re.search(r'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"',
                      source.read_text(encoding="utf-8-sig"))
    if not match or not re.fullmatch(VERSION_PATTERN, match[1]):
        raise ReleaseError(f"Expected a Major.Minor.Patch plugin Version in {source}")
    return match[1]


def read_manifest(root, version):
    manifest = json.loads((root / "thunderstore/package.json").read_text(encoding="utf-8-sig"))
    if not isinstance(manifest, dict) or set(manifest) != {"name", "description", "website_url", "dependencies"}:
        raise ReleaseError("package.json needs name, description, website_url and dependencies; version comes from the plugin")
    name = manifest["name"]
    if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9_]{1,128}", name):
        raise ReleaseError("Package names allow only letters, numbers and underscores (1-128 characters)")
    description = manifest["description"]
    if not isinstance(description, str) or not 1 <= len(description) <= 250:
        raise ReleaseError("Package description must contain 1-250 characters")
    website = manifest["website_url"]
    if not isinstance(website, str) or (website and not re.fullmatch(r"https?://[^\s]+", website)):
        raise ReleaseError("website_url must be an HTTP(S) URL or an empty string")
    dependencies = manifest["dependencies"]
    if not isinstance(dependencies, list) or not dependencies:
        raise ReleaseError("An explicit BepInEx dependency is required")
    for dependency in dependencies:
        if not isinstance(dependency, str) or not re.fullmatch(
                rf"[A-Za-z0-9_]+-[A-Za-z0-9_]+-{VERSION_PATTERN}", dependency):
            raise ReleaseError(f"Invalid Thunderstore dependency: {dependency!r}")
    if not any(item.startswith("BepInEx-BepInExPack_GTFO-") for item in dependencies):
        raise ReleaseError("dependencies must explicitly include BepInEx-BepInExPack_GTFO")
    return {**manifest, "version_number": version}


def validate_icon(data):
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ReleaseError("icon.png must be a PNG image")
    offset = 8
    kinds = []
    while offset + 12 <= len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        end = offset + 12 + length
        if end > len(data):
            raise ReleaseError("icon.png contains a truncated PNG chunk")
        kind = data[offset + 4:offset + 8]
        payload = data[offset + 8:end - 4]
        crc = struct.unpack_from(">I", data, end - 4)[0]
        if crc != binascii.crc32(kind + payload) & 0xffffffff:
            raise ReleaseError("icon.png contains a corrupt PNG chunk")
        if not kinds:
            if kind != b"IHDR" or length != 13:
                raise ReleaseError("icon.png has an invalid PNG header")
            if struct.unpack_from(">II", payload) != (256, 256):
                raise ReleaseError("Thunderstore requires an exactly 256x256 icon.png")
        kinds.append(kind)
        offset = end
        if kind == b"IEND":
            if payload or offset != len(data) or b"IDAT" not in kinds:
                raise ReleaseError("icon.png has an invalid PNG ending")
            return
    raise ReleaseError("icon.png has no complete PNG ending")


def package_files(root, manifest, dll, icon=None):
    binary = dll.read_bytes()
    if not binary.startswith(b"MZ"):
        raise ReleaseError(f"Not a Windows DLL: {dll}")
    readme = (root / "thunderstore/README.md").read_bytes()
    if not readme.decode("utf-8-sig").strip():
        raise ReleaseError("thunderstore/README.md is empty")
    files = {}
    for filename in SCREENSHOTS:
        source = root / "thunderstore/images" / filename
        if source.is_file():
            data = source.read_bytes()
            if not data.startswith(b"\x89PNG\r\n\x1a\n"):
                raise ReleaseError(f"Screenshot must be a PNG: {source}")
            target = f"images/{filename}"
            files[target] = data
    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8-sig")
    if not re.search(rf"^##\s+{re.escape(manifest['version_number'])}\s*$", changelog, re.MULTILINE):
        raise ReleaseError("CHANGELOG.md needs a heading for the plugin version")
    icon_path = icon if icon is not None else root / "thunderstore/images/icon.png"
    if not icon_path.is_file():
        raise ReleaseError(f"Release icon not found: {icon_path}. Supply a 256x256 PNG or use --icon.")
    image = icon_path.read_bytes()
    validate_icon(image)
    files.update({
        "manifest.json": (json.dumps(manifest, indent=2, ensure_ascii=False) + "\n").encode("utf-8"),
        "README.md": readme,
        "CHANGELOG.md": (changelog.strip() + "\n").encode("utf-8"),
        "icon.png": image,
        # The mod manager supplies its own Author-Package installation folder.
        # A root DLL avoids duplicating that folder inside the package directory.
        ASSEMBLY: binary,
    })
    return files


def windows_path(path):
    value = str(path)
    if re.match(r"^[A-Za-z]:[\\/]", value):
        return value
    match = re.fullmatch(r"/mnt/([A-Za-z])/(.*)", value)
    if not match:
        raise ReleaseError(f"Windows build needs a drive-mounted path, not {path}")
    return match[1].upper() + ":\\" + match[2].replace("/", "\\")


def build_release(root, gtfo_path=None):
    project = root / "GTFO_PersistentCheckpoint/GTFO_PersistentCheckpoint.csproj"
    arguments = ["build", str(project), "--configuration", "Release",
                 "-p:DeployPlugin=false", "-v:minimal"]
    environment = {**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "DOTNET_NOLOGO": "1"}
    if os.name != "nt" and shutil.which("cmd.exe") and str(root).startswith("/mnt/"):
        # Feed cmd via stdin, avoiding WSL's fragile /c quoting for spaced paths.
        arguments[1] = windows_path(project)
        if gtfo_path:
            arguments.append("-p:GTFO_PATH=" + windows_path(gtfo_path))
        dotnet = shutil.which("dotnet.exe")
        executable = windows_path(Path(dotnet)) if dotnet else r"C:\Program Files\dotnet\dotnet.exe"
        script = (f'cd /d "{windows_path(root)}"\n'
                  + subprocess.list2cmdline([executable, *arguments])
                  + "\nexit %errorlevel%\n")
        subprocess.run(["cmd.exe", "/d", "/q", "/k"], input=script,
                       text=True, env=environment, check=True)
    else:
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            raise ReleaseError("dotnet SDK not found; install it or use --no-build with an existing Release DLL")
        if gtfo_path:
            arguments.append("-p:GTFO_PATH=" + str(gtfo_path))
        subprocess.run([dotnet, *arguments], env=environment, check=True)


def write_archive(files, destination):
    # Fixed timestamps/order make packaging the same inputs reproducible.
    with zipfile.ZipFile(destination, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, data in sorted(files.items()):
            entry = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.create_system = 3
            entry.external_attr = 0o100644 << 16
            archive.writestr(entry, data, compresslevel=9)
    with zipfile.ZipFile(destination) as archive:
        if archive.testzip() is not None or set(archive.namelist()) != set(files):
            raise ReleaseError("Generated ZIP failed validation")


def create_package(root, output_dir, dll=None, icon=None, keep=False):
    version = read_version(root)
    manifest = read_manifest(root, version)
    dll = dll if dll is not None else root / "GTFO_PersistentCheckpoint/bin/Release" / ASSEMBLY
    files = package_files(root, manifest, dll, icon)
    output_dir.mkdir(parents=True, exist_ok=True)
    destination = output_dir / f"{manifest['name']}-{version}.zip"
    # Only clean a directory created by this invocation, never a shared Staging.
    with tempfile.TemporaryDirectory(prefix="checkpoint-release-", dir=output_dir) as temporary:
        staged_zip = Path(temporary) / "release.zip"
        write_archive(files, staged_zip)
        os.replace(staged_zip, destination)
    if keep:
        staging = Path(tempfile.mkdtemp(prefix=f"{manifest['name']}-{version}-", dir=output_dir))
        for name, data in files.items():
            target = staging / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
        print(f"Generated files: {staging}")
    print(f"Created {destination} ({destination.stat().st_size:,} bytes)")
    print(f"Version: {version}; dependencies: {', '.join(manifest['dependencies'])}")
    print("ZIP contents:\n  " + "\n  ".join(sorted(files)))
    return destination


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-build", "--skip-build", action="store_true",
                        help="Package an already-built Release DLL (you are responsible for its freshness)")
    parser.add_argument("--dll", type=Path, help="Use this DLL instead; requires --no-build")
    parser.add_argument("--icon", type=Path, help="Use a custom 256x256 PNG icon")
    parser.add_argument("--output-dir", type=Path, default=PROJECT_ROOT / "dist")
    parser.add_argument("--gtfo-path", type=Path, help="Override the game's path when building")
    parser.add_argument("-k", "--keep", action="store_true", help="Also keep generated files in a unique staging directory")
    args = parser.parse_args(argv)
    if args.dll is not None and not args.no_build:
        parser.error("--dll requires --no-build")
    try:
        read_manifest(PROJECT_ROOT, read_version(PROJECT_ROOT))
        if not args.no_build:
            build_release(PROJECT_ROOT, args.gtfo_path)
        create_package(PROJECT_ROOT, args.output_dir, args.dll, args.icon, args.keep)
    except (ReleaseError, OSError, ValueError, subprocess.CalledProcessError) as exception:
        print(f"Release failed: {exception}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
