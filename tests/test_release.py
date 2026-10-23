"""Packaging tests; all writes use test-owned temporary directories, not game data."""

from contextlib import redirect_stdout
import binascii
import io
import json
from pathlib import Path, PurePosixPath
import struct
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, str(Path(__file__).absolute().parents[1]))
import create_release as release


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="gtfo-release-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "GTFO_PersistentCheckpoint/Core/PersistentCheckpointPlugin.cs"
        self.source.parent.mkdir(parents=True)
        self.source.write_text('public const string Version = "1.2.3";', encoding="utf-8")
        self.config = {
            "name": "PersistentCheckpoint",
            "description": "Resume expeditions across sessions.",
            "website_url": "",
            "dependencies": ["BepInEx-BepInExPack_GTFO-3.2.3"],
        }
        (self.root / "thunderstore/images").mkdir(parents=True)
        self.icon_data = (release.PROJECT_ROOT / "thunderstore/images/icon.png").read_bytes()
        self.icon = self.root / "thunderstore/images/icon.png"
        self.icon.write_bytes(self.icon_data)
        self.write_config()
        (self.root / "README.md").write_text("# PRIVATE DEVELOPER NOTES\n", encoding="utf-8")
        self.readme = self.root / "thunderstore/README.md"
        self.readme.write_text("# Checkpoints\n\nInstructions.\n", encoding="utf-8")
        (self.root / "CHANGELOG.md").write_text("# Changelog\n\n## 1.2.3\n\n- Test.\n", encoding="utf-8")
        self.dll = self.root / "GTFO_PersistentCheckpoint/bin/Release" / release.ASSEMBLY
        self.dll.parent.mkdir(parents=True)
        # Only a package-content fixture, not an executable plugin.
        self.dll.write_bytes(b"MZ-test-plugin")

    def write_config(self):
        (self.root / "thunderstore/package.json").write_text(json.dumps(self.config), encoding="utf-8")

    def files(self, **kwargs):
        version = release.read_version(self.root)
        return release.package_files(self.root, release.read_manifest(self.root, version), self.dll, **kwargs)

    def package(self, **kwargs):
        with redirect_stdout(io.StringIO()):
            return release.create_package(self.root, self.root / "dist", **kwargs)

    def screenshot(self, name="resume-checkpoint.png"):
        (self.root / "thunderstore/images" / name).write_bytes(self.icon_data)

    def test_version_and_dependency_are_in_manifest(self):
        manifest = json.loads(self.files()["manifest.json"])
        self.assertEqual(manifest["version_number"], "1.2.3")
        self.assertEqual(manifest["dependencies"], self.config["dependencies"])
        self.assertEqual(manifest["website_url"], "")

    def test_invalid_source_versions_are_rejected(self):
        for version in ("1.2", "v1.2.3", "1.2.3.4", "1.2.3-beta", "01.2.3"):
            with self.subTest(version=version):
                self.source.write_text(f'public const string Version = "{version}";', encoding="utf-8")
                with self.assertRaises(release.ReleaseError):
                    release.read_version(self.root)

    def test_manifest_rejects_bad_names_descriptions_and_dependencies(self):
        cases = (
            ("name", "Not a package"), ("name", "../../escape"),
            ("description", "x" * 251), ("description", ""),
            ("dependencies", []), ("dependencies", ["Other-Mod-1.0.0"]),
            ("dependencies", ["BepInEx-BepInExPack_GTFO-latest"]),
            ("website_url", "not a URL"),
        )
        for field, value in cases:
            with self.subTest(field=field, value=value):
                original = self.config[field]
                self.config[field] = value
                self.write_config()
                with self.assertRaises(release.ReleaseError):
                    release.read_manifest(self.root, "1.2.3")
                self.config[field] = original

    def test_changelog_must_cover_plugin_version(self):
        (self.root / "CHANGELOG.md").write_text("## 1.0.0\n- Old.\n", encoding="utf-8")
        with self.assertRaisesRegex(release.ReleaseError, "CHANGELOG"):
            self.files()

    def test_dedicated_thunderstore_readme_is_shipped(self):
        readme = self.files()["README.md"].decode()
        self.assertIn("Instructions.", readme)
        self.assertNotIn("PRIVATE", readme)

    def test_missing_release_readme_never_falls_back_to_repository_readme(self):
        self.readme.unlink()
        with self.assertRaises(FileNotFoundError):
            self.files()

    def test_empty_release_readme_is_rejected(self):
        self.readme.write_text(" \n", encoding="utf-8")
        with self.assertRaisesRegex(release.ReleaseError, "thunderstore/README.md is empty"):
            self.files()

    def test_supplied_icon_is_valid_and_packaged_verbatim(self):
        release.validate_icon(self.icon_data)
        self.assertEqual(struct.unpack_from(">II", self.icon_data, 16), (256, 256))
        self.assertEqual(self.files()["icon.png"], self.icon_data)

    def test_corrupt_or_wrong_size_icons_are_rejected(self):
        data = self.icon_data
        bad_crc = bytearray(data)
        bad_crc[20] ^= 1
        wrong_size = bytearray(data)
        struct.pack_into(">I", wrong_size, 16, 128)
        struct.pack_into(">I", wrong_size, 29, binascii.crc32(wrong_size[12:29]) & 0xffffffff)
        for value in (b"not a PNG", data[:-8], bytes(bad_crc), wrong_size, data + b"garbage"):
            with self.subTest(length=len(value)):
                with self.assertRaises(release.ReleaseError):
                    release.validate_icon(value)

    def test_invalid_explicit_icon_is_rejected(self):
        override = self.root / "override.png"
        override.write_bytes(b"invalid")
        with self.assertRaises(release.ReleaseError):
            self.files(icon=override)

    def test_missing_required_icon_is_rejected(self):
        self.icon.unlink()
        with self.assertRaisesRegex(release.ReleaseError, "Release icon not found"):
            self.files()

    def test_explicit_icon_can_replace_missing_default(self):
        self.icon.unlink()
        override = self.root / "override.png"
        override.write_bytes(self.icon_data)
        self.assertEqual(self.files(icon=override)["icon.png"], self.icon_data)

    def test_optional_screenshots_are_bundled_without_modifying_readme(self):
        for name in release.SCREENSHOTS:
            self.screenshot(name)
        files = self.files()
        for name in release.SCREENSHOTS:
            self.assertEqual(files[f"images/{name}"], self.icon_data)
        self.assertEqual(files["README.md"], self.readme.read_bytes())

    def test_invalid_screenshot_is_rejected(self):
        (self.root / "thunderstore/images/resume-checkpoint.png").write_bytes(b"not a PNG")
        with self.assertRaisesRegex(release.ReleaseError, "Screenshot must be a PNG"):
            self.files()

    def test_release_readme_is_copied_byte_for_byte(self):
        data = b"# Checkpoints\r\n\r\n![Screenshot](https://images.example.org/test.png)\r\n\r\n"
        self.readme.write_bytes(data)
        self.assertEqual(self.files()["README.md"], data)

    def test_missing_optional_images_still_build(self):
        self.assertEqual(len(self.files()), 5)

    def test_only_release_allowlist_is_zipped(self):
        (self.root / "checkpoint.pcp").write_bytes(b"DO NOT SHIP")
        (self.dll.parent / "UnrelatedPlugin.dll").write_bytes(b"MZ-unrelated")
        result = self.package()
        with zipfile.ZipFile(result) as archive:
            self.assertEqual(set(archive.namelist()), {
                "manifest.json", "README.md", "CHANGELOG.md", "icon.png",
                "TheRoughy.PersistentCheckpoint.dll",
            })
            self.assertEqual(archive.read("TheRoughy.PersistentCheckpoint.dll"),
                             self.dll.read_bytes())
            self.assertIsNone(archive.testzip())

    def test_assembly_filename_matches_actual_project(self):
        project = ET.parse(release.PROJECT_ROOT / "GTFO_PersistentCheckpoint/GTFO_PersistentCheckpoint.csproj")
        self.assertEqual(project.findtext("PropertyGroup/AssemblyName") + ".dll", release.ASSEMBLY)
        destination = project.find("Target[@Name='Deploy']/Copy").get("DestinationFolder")
        self.assertEqual(destination, r"$(GTFO_PATH)\BepInEx\plugins\TheRoughy-PersistentCheckpoint")

    def test_package_is_reproducible_and_does_not_collect_old_output(self):
        first = self.package().read_bytes()
        (self.root / "dist/old-file.txt").write_text("not included", encoding="utf-8")
        self.assertEqual(self.package().read_bytes(), first)

    def test_failure_preserves_previous_release_and_user_directories(self):
        result = self.package()
        original = result.read_bytes()
        staging = self.root / "Staging"
        staging.mkdir()
        marker = staging / "user-file.txt"
        marker.write_text("keep", encoding="utf-8")
        self.dll.write_bytes(b"invalid")
        with self.assertRaises(release.ReleaseError):
            self.package()
        self.assertEqual(result.read_bytes(), original)
        self.assertEqual(marker.read_text(), "keep")

    def test_keep_outputs_generated_files_in_unique_directory(self):
        self.package(keep=True)
        staging = list((self.root / "dist").glob("PersistentCheckpoint-1.2.3-*/manifest.json"))
        self.assertEqual(len(staging), 1)

    def test_missing_required_dll_does_not_package_another_plugin(self):
        self.dll.rename(self.dll.with_name("UnrelatedPlugin.dll"))
        with self.assertRaises(FileNotFoundError):
            self.package()

    def test_windows_path_conversion_keeps_spaces(self):
        self.assertEqual(release.windows_path(PurePosixPath("/mnt/c/Users/Roughy/Project With Spaces")),
                         "C:\\Users\\Roughy\\Project With Spaces")
        self.assertEqual(release.windows_path(Path("C:\\GTFO")), "C:\\GTFO")
        with self.assertRaises(release.ReleaseError):
            release.windows_path(Path("/tmp/project"))

    def test_build_disables_deployment_and_propagates_failures(self):
        def which(name):
            return "/usr/bin/dotnet" if name == "dotnet" else None

        with patch.object(release.shutil, "which", side_effect=which), \
                patch.object(release.subprocess, "run") as run:
            release.build_release(self.root)
            self.assertIn("-p:DeployPlugin=false", run.call_args.args[0])
            self.assertTrue(run.call_args.kwargs["check"])
            run.side_effect = subprocess.CalledProcessError(1, "dotnet")
            with self.assertRaises(subprocess.CalledProcessError):
                release.build_release(self.root)

    def test_wsl_build_feeds_quoted_windows_command_to_stdin(self):
        def which(name):
            return "/mnt/c/Windows/System32/cmd.exe" if name == "cmd.exe" else None

        with patch.object(release.os, "name", "posix"), \
                patch.object(release.shutil, "which", side_effect=which), \
                patch.object(release.subprocess, "run") as run:
            release.build_release(PurePosixPath("/mnt/c/Project With Spaces"))
            self.assertEqual(run.call_args.args[0], ["cmd.exe", "/d", "/q", "/k"])
            script = run.call_args.kwargs["input"]
            self.assertIn('cd /d "C:\\Project With Spaces"', script)
            self.assertIn('"C:\\Program Files\\dotnet\\dotnet.exe"', script)
            self.assertIn("-p:DeployPlugin=false", script)
            self.assertTrue(script.endswith("exit %errorlevel%\n"))


if __name__ == "__main__":
    unittest.main()
