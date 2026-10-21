#!/usr/bin/env python3
"""Compose the main prerelease notes with per-platform change lists."""

from __future__ import annotations

import argparse
import os
import re
import subprocess
import sys
from pathlib import Path


ZERO_SHA = "0" * 40
SHA_PATTERN = re.compile(r"[0-9a-fA-F]{40}")
TAG_PREFIX = "main-"
MAC_DIRECTORY = "macos/"
WINDOWS_DIRECTORY = "windows/"
MAX_COMMITS = 20


class NotesError(RuntimeError):
    pass


def git(repo: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", *args], cwd=repo, text=True, encoding="utf-8", errors="replace",
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    if result.returncode:
        detail = result.stderr.strip() or f"git {' '.join(args)} exited {result.returncode}"
        raise NotesError(detail)
    return result.stdout


def succeeds(repo: Path, *args: str) -> bool:
    return subprocess.run(
        ["git", *args], cwd=repo, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    ).returncode == 0


def is_ancestor(repo: Path, ancestor: str, descendant: str) -> bool:
    return succeeds(repo, "merge-base", "--is-ancestor", ancestor, descendant)


def previous_tag_sha(repo: Path, after: str) -> str | None:
    """Return the newest commit tagged `main-...` that is an ancestor of `after`."""
    best: tuple[str, int] | None = None
    for line in git(
        repo, "for-each-ref",
        "--format=%(objectname)%09%(*objectname)%09%(creatordate:unix)",
        f"refs/tags/{TAG_PREFIX}*",
    ).splitlines():
        direct, peeled, date = (line.split("\t") + ["", ""])[:3]
        sha = peeled or direct
        if sha == after or not is_ancestor(repo, sha, after):
            continue
        best_date = int(date or "0")
        if best is None or best_date > best[1] or (
            best_date == best[1] and is_ancestor(repo, best[0], sha)
        ):
            best = (sha, best_date)
    return best[0] if best else None


def resolve_base(repo: Path, after: str, before: str | None) -> str | None:
    tagged = previous_tag_sha(repo, after)
    if tagged is not None:
        return tagged
    if (
        before
        and SHA_PATTERN.fullmatch(before)
        and before != ZERO_SHA
        and before.lower() != after
        and is_ancestor(repo, before, after)
    ):
        return before
    return None


def commits_touching(repo: Path, base: str | None, after: str, directory: str) -> list[tuple[str, str]]:
    rev = after if base is None else f"{base}..{after}"
    output = git(repo, "log", "--no-merges", "--format=%H%x09%s", rev, "--", directory)
    commits: list[tuple[str, str]] = []
    for line in output.splitlines():
        sha, tab, subject = line.partition("\t")
        if tab and SHA_PATTERN.fullmatch(sha):
            commits.append((sha, subject))
    return commits


def commit_word(count: int) -> str:
    if count % 10 == 1 and count % 100 != 11:
        return "коммит"
    if count % 10 in (2, 3, 4) and count % 100 not in (12, 13, 14):
        return "коммита"
    return "коммитов"


def section(title: str, commits: list[tuple[str, str]]) -> list[str]:
    lines = [f"## {title}", ""]
    if not commits:
        lines.append("Изменений нет.")
        return lines
    shown, hidden = commits[:MAX_COMMITS], commits[MAX_COMMITS:]
    for sha, subject in shown:
        lines.append(f"- `{sha[:12]}` {subject}")
    if hidden:
        lines.append(f"- и ещё {len(hidden)} {commit_word(len(hidden))}")
    return lines


def build_notes(repo: Path, after: str, before: str | None) -> str:
    base = resolve_base(repo, after, before)
    windows = commits_touching(repo, base, after, WINDOWS_DIRECTORY)
    macos = commits_touching(repo, base, after, MAC_DIRECTORY)
    lines = [
        f"Сборки macOS и Windows из коммита {after} ветки main.",
        "",
        *section("Изменения в Windows", windows),
        "",
        "---",
        "",
        *section("Изменения в macOS", macos),
        "",
        "Это проверочный prerelease. Стабильные релизы публикуются отдельно после повышения версии соответствующей платформы.",
    ]
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--after", default=os.environ.get("GITHUB_SHA"))
    parser.add_argument("--before", default=os.environ.get("PUSH_BEFORE") or None)
    parser.add_argument("--repository", default=".")
    args = parser.parse_args()
    if not args.after or not SHA_PATTERN.fullmatch(args.after):
        parser.error("--after or GITHUB_SHA must be a commit SHA")
    try:
        notes = build_notes(Path(args.repository).resolve(), args.after.lower(), args.before)
    except NotesError as error:
        print(f"prerelease notes: {error}", file=sys.stderr)
        return 1
    sys.stdout.buffer.write(notes.encode("utf-8"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
