from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "detect_release.py"


class GitFixture:
    def __init__(self, root: Path):
        self.root = root
        self.run("init", "-q", "-b", "main")
        self.run("config", "user.name", "Release gate test")
        self.run("config", "user.email", "release-gate@example.invalid")

    def run(self, *args: str) -> str:
        result = subprocess.run(
            ["git", *args], cwd=self.root, text=True, encoding="utf-8",
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        )
        if result.returncode:
            raise AssertionError(result.stderr)
        return result.stdout.strip()

    def write(self, path: str, content: str) -> None:
        file = self.root / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(content, encoding="utf-8")

    def mac_version(self, version: str) -> None:
        self.write("macos/Hako/Version.xcconfig", f"MARKETING_VERSION = {version}\n")

    def windows_version(self, version: str) -> None:
        self.write(
            "windows/Hako/Package.appxmanifest",
            '<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">'
            f'<Identity Name="test" Publisher="CN=daske" Version="{version}" />'
            "</Package>\n",
        )

    def commit(self, message: str) -> str:
        self.run("add", "-A")
        self.run("commit", "-q", "-m", message)
        return self.run("rev-parse", "HEAD")


class ReleaseGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.repo = Path(self.temp.name)
        self.git = GitFixture(self.repo)
        self.git.mac_version("1.0.0")
        self.git.windows_version("1.0.0.0")
        self.git.write("macos/Hako/Sources/App.swift", "let app = true\n")
        self.git.write("windows/Hako/Program.cs", "class Program {}\n")
        self.base = self.git.commit("initial")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def detect(self, before: str, after: str) -> dict[str, str]:
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--before", before, "--after", after],
            cwd=self.repo, text=True, encoding="utf-8",
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def failure_message(self, before: str, after: str) -> str:
        result = subprocess.run(
            [sys.executable, str(SCRIPT), "--before", before, "--after", after],
            cwd=self.repo, text=True, encoding="utf-8",
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        )
        self.assertNotEqual(result.returncode, 0)
        return result.stderr

    def test_mac_only_and_windows_only_version_bumps(self) -> None:
        self.git.mac_version("1.0.1")
        mac = self.git.commit("bump macOS")
        self.assertEqual(self.detect(self.base, mac)["macos"], "true")
        self.assertEqual(self.detect(self.base, mac)["windows"], "false")

        self.git.windows_version("1.0.1.0")
        windows = self.git.commit("bump Windows")
        result = self.detect(mac, windows)
        self.assertEqual((result["macos"], result["windows"]), ("false", "true"))

    def test_both_platforms_are_independent_and_eligible(self) -> None:
        self.git.mac_version("1.0.1")
        self.git.windows_version("1.0.1.0")
        result = self.detect(self.base, self.git.commit("bump both"))
        self.assertEqual((result["macos"], result["windows"]), ("true", "true"))
        self.assertEqual(result["macos_tag"], "macos-v1.0.1")
        self.assertEqual(result["windows_tag"], "windows-v1.0.1")

    def test_code_without_bump_rollback_build_number_and_docs_do_not_release(self) -> None:
        self.git.write("macos/Hako/Sources/App.swift", "let app = false\n")
        no_bump = self.git.commit("macOS source only")
        self.assertEqual(self.detect(self.base, no_bump)["macos"], "false")

        self.git.mac_version("0.9.9")
        rollback = self.git.commit("rollback")
        self.assertEqual(self.detect(no_bump, rollback)["macos"], "false")

        self.git.write("macos/Hako/Hako.xcodeproj/project.pbxproj", "CURRENT_PROJECT_VERSION = 2;\n")
        build_number = self.git.commit("build number only")
        self.assertEqual(self.detect(rollback, build_number)["macos"], "false")

        self.git.write("README.md", "documentation only\n")
        docs = self.git.commit("docs")
        result = self.detect(build_number, docs)
        self.assertEqual((result["macos"], result["windows"]), ("false", "false"))

    def test_version_only_change_qualifies(self) -> None:
        self.git.mac_version("1.0.1")
        result = self.detect(self.base, self.git.commit("version only"))
        self.assertEqual(result["macos"], "true")

    def test_multiple_commits_are_compared_against_push_before(self) -> None:
        self.git.write("macos/Hako/Sources/App.swift", "let app = false\n")
        self.git.commit("first commit")
        self.git.mac_version("1.0.1")
        after = self.git.commit("second commit")
        self.assertEqual(self.detect(self.base, after)["macos"], "true")

    def test_merge_commit_diff_uses_push_before_and_full_result_tree(self) -> None:
        self.git.run("checkout", "-q", "-b", "feature")
        self.git.windows_version("1.0.1.0")
        self.git.commit("feature version")
        self.git.run("checkout", "-q", "main")
        self.git.write("README.md", "main side\n")
        self.git.commit("main work")
        self.git.run("merge", "-q", "--no-ff", "--no-edit", "feature")
        after = self.git.run("rev-parse", "HEAD")
        result = self.detect(self.base, after)
        self.assertEqual((result["macos"], result["windows"]), ("false", "true"))

    def test_large_mixed_diff_is_not_truncated_to_a_file_list_limit(self) -> None:
        for index in range(3001):
            self.git.write(f"docs/generated/{index:04}.md", "documentation\n")
        self.git.windows_version("1.0.1.0")
        after = self.git.commit("large mixed change")
        result = self.detect(self.base, after)
        self.assertEqual((result["macos"], result["windows"]), ("false", "true"))

    def test_numeric_version_comparison_handles_two_digit_minor(self) -> None:
        self.git.mac_version("1.9.9")
        before = self.git.commit("set old version")
        self.git.mac_version("1.10.0")
        after = self.git.commit("increase minor")
        self.assertEqual(self.detect(before, after)["macos_version"], "1.10.0")
        self.assertEqual(self.detect(before, after)["macos"], "true")

    def test_legacy_mac_version_normalizes_before_comparison(self) -> None:
        self.git.root.joinpath("macos/Hako/Version.xcconfig").unlink()
        self.git.write(
            "macos/Hako/Hako.xcodeproj/project.pbxproj",
            "MARKETING_VERSION = 1.0;\nMARKETING_VERSION = 1.0;\n",
        )
        before = self.git.commit("legacy version")
        self.git.mac_version("1.0.0")
        after = self.git.commit("normalize version")
        result = self.detect(before, after)
        self.assertEqual((result["macos"], result["macos_version"]), ("false", "1.0.0"))

    def test_new_platform_and_zero_before_are_first_versions(self) -> None:
        with tempfile.TemporaryDirectory() as isolated:
            repo = Path(isolated)
            git = GitFixture(repo)
            git.mac_version("1.0.0")
            git.write("macos/Hako/Sources/App.swift", "let app = true\n")
            first = git.commit("macOS only")
            git.windows_version("1.0.0.0")
            git.write("windows/Hako/Program.cs", "class Program {}\n")
            second = git.commit("add Windows platform")
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--before", first, "--after", second],
                cwd=repo, text=True, encoding="utf-8", stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(json.loads(result.stdout)["windows"], "true")

        initial = self.detect("0" * 40, self.base)
        self.assertEqual((initial["macos"], initial["windows"]), ("true", "true"))

    def test_malformed_versions_and_missing_before_fail_clearly(self) -> None:
        self.git.mac_version("1.0-beta")
        malformed = self.git.commit("malformed version")
        self.assertIn("Malformed macOS after version", self.failure_message(self.base, malformed))
        self.assertIn("no valid `before` SHA", self.failure_message("", malformed))
        self.assertIn("Cannot read push before commit", self.failure_message("f" * 40, malformed))

        self.git.windows_version("1.0.0.1")
        malformed_windows = self.git.commit("malformed Windows version")
        self.assertIn("Malformed Windows after version", self.failure_message(malformed, malformed_windows))


if __name__ == "__main__":
    unittest.main()
