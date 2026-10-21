from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "prerelease_notes.py"


def run_notes(
    repository: Path, after: str | None, before: str = ""
) -> subprocess.CompletedProcess[str]:
    environment = os.environ.copy()
    environment.pop("GITHUB_SHA", None)
    environment.pop("PUSH_BEFORE", None)
    command = [sys.executable, str(SCRIPT)]
    if after is not None:
        command += ["--after", after]
    if before:
        command += ["--before", before]
    return subprocess.run(
        command, cwd=repository, text=True, encoding="utf-8",
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=environment,
    )


class GitFixture:
    def __init__(self, root: Path):
        self.root = root
        self.run("init", "-q", "-b", "main")
        self.run("config", "user.name", "Prerelease notes test")
        self.run("config", "user.email", "prerelease-notes@example.invalid")

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

    def commit(self, message: str) -> str:
        self.run("add", "-A")
        self.run("commit", "-q", "-m", message)
        return self.run("rev-parse", "HEAD")

    def tag(self, name: str) -> None:
        self.run("tag", name)


class PrereleaseNotesTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.repo = Path(self.temp.name)
        self.git = GitFixture(self.repo)
        self.git.write("macos/Hako/Sources/App.swift", "let app = true\n")
        self.git.write("windows/Hako/Program.cs", "class Program {}\n")
        self.base = self.git.commit("initial")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def notes(self, after: str, before: str = "") -> str:
        result = run_notes(self.repo, after, before)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout

    def section_between(self, notes: str, title: str) -> str:
        start = notes.index(f"## {title}") + len(f"## {title}")
        tail = notes[start:]
        ends = [position for marker in ("---", "Это проверочный prerelease") if marker in tail
                for position in [tail.index(marker)]]
        return tail[:min(ends)]

    def test_change_lists_are_split_between_platforms(self) -> None:
        self.git.write("windows/Hako/Program.cs", "updated\n")
        after = self.git.commit("Добавить профиль Minecraft в Windows")
        notes = self.notes(after, before=self.base)
        self.assertIn(f"Сборки macOS и Windows из коммита {after} ветки main.", notes)
        self.assertIn("Это проверочный prerelease. Стабильные релизы публикуются отдельно", notes)
        windows = self.section_between(notes, "Изменения в Windows")
        macos = self.section_between(notes, "Изменения в macOS")
        self.assertIn("Добавить профиль Minecraft в Windows", windows)
        self.assertIn("Изменений нет.", macos)
        self.assertNotIn("Добавить профиль", macos)

    def test_previous_main_tag_limits_the_range(self) -> None:
        self.git.write("macos/Hako/Sources/App.swift", "v2\n")
        first = self.git.commit("Изменение macOS до релиза")
        self.git.tag(f"main-{first}")
        self.git.write("windows/Hako/Program.cs", "v2\n")
        self.git.commit("Изменение Windows после релиза")
        self.git.write("macos/Hako/Sources/App.swift", "v3\n")
        after = self.git.commit("Изменение macOS после релиза")
        notes = self.notes(after)
        self.assertIn("Изменение Windows после релиза", notes)
        self.assertIn("Изменение macOS после релиза", notes)
        self.assertNotIn("Изменение macOS до релиза", notes)

    def test_older_tag_does_not_widen_the_range(self) -> None:
        self.git.write("macos/Hako/Sources/App.swift", "v2\n")
        first = self.git.commit("первое изменение")
        self.git.tag(f"main-{first}")
        self.git.write("windows/Hako/Program.cs", "v2\n")
        second = self.git.commit("второе изменение")
        self.git.tag(f"main-{second}")
        self.git.write("macos/Hako/Sources/App.swift", "v3\n")
        after = self.git.commit("третье изменение")
        notes = self.notes(after)
        self.assertNotIn("первое изменение", notes)
        self.assertNotIn("второе изменение", notes)
        self.assertIn("третье изменение", notes)

    def test_commit_touching_both_platforms_is_listed_in_both(self) -> None:
        self.git.write("macos/Hako/Sources/App.swift", "v2\n")
        self.git.write("windows/Hako/Program.cs", "v2\n")
        after = self.git.commit("Общее изменение платформ")
        notes = self.notes(after)
        self.assertIn("Общее изменение платформ", self.section_between(notes, "Изменения в Windows"))
        self.assertIn("Общее изменение платформ", self.section_between(notes, "Изменения в macOS"))

    def test_falls_back_to_push_before_without_tags(self) -> None:
        self.git.write("windows/Hako/Program.cs", "v2\n")
        before = self.git.commit("старое изменение Windows")
        self.git.write("macos/Hako/Sources/App.swift", "v2\n")
        after = self.git.commit("новое изменение macOS")
        notes = self.notes(after, before=before)
        self.assertIn("новое изменение macOS", self.section_between(notes, "Изменения в macOS"))
        self.assertIn("Изменений нет.", self.section_between(notes, "Изменения в Windows"))
        self.assertNotIn("старое изменение Windows", notes)

    def test_without_base_lists_all_platform_commits(self) -> None:
        notes = self.notes(self.base)
        self.assertIn("initial", self.section_between(notes, "Изменения в Windows"))
        self.assertIn("initial", self.section_between(notes, "Изменения в macOS"))

    def test_merge_commits_are_not_listed(self) -> None:
        self.git.run("checkout", "-q", "-b", "feature")
        self.git.write("windows/Hako/Program.cs", "v2\n")
        self.git.commit("изменение в ветке")
        self.git.run("checkout", "-q", "main")
        self.git.write("macos/Hako/Sources/App.swift", "v2\n")
        self.git.commit("изменение в main")
        self.git.run("merge", "-q", "--no-ff", "--no-edit", "feature")
        after = self.git.run("rev-parse", "HEAD")
        notes = self.notes(after)
        self.assertIn("изменение в ветке", self.section_between(notes, "Изменения в Windows"))
        self.assertIn("изменение в main", self.section_between(notes, "Изменения в macOS"))
        self.assertNotIn("Merge", notes)

    def test_hidden_commits_beyond_the_cap_are_counted(self) -> None:
        for index in range(22):
            self.git.write(f"windows/Hako/Gen{index}.cs", f"// {index}\n")
            self.git.commit(f"изменение Windows {index}")
        self.git.write("docs/readme.md", "not a platform change\n")
        after = self.git.commit("финальное изменение")
        notes = self.notes(after)
        windows = self.section_between(notes, "Изменения в Windows")
        self.assertIn("изменение Windows 21", windows)
        self.assertNotIn("изменение Windows 0\n", windows)
        self.assertIn("и ещё 3 коммита", windows)

    def test_missing_after_fails_clearly(self) -> None:
        result = run_notes(self.repo, None)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--after or GITHUB_SHA must be a commit SHA", result.stderr)

    def test_annotated_tag_is_peeled_to_the_commit(self) -> None:
        self.git.write("macos/Hako/Sources/App.swift", "v2\n")
        first = self.git.commit("изменение до релиза")
        self.git.run("tag", "-a", f"main-{first}", "-m", "release")
        self.git.write("macos/Hako/Sources/App.swift", "v3\n")
        after = self.git.commit("изменение после релиза")
        notes = self.notes(after)
        self.assertIn("изменение после релиза", notes)
        self.assertNotIn("изменение до релиза", notes)


if __name__ == "__main__":
    unittest.main()
