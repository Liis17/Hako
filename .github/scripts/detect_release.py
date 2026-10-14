#!/usr/bin/env python3
"""Determine which platform release builds are eligible for a push."""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


ZERO_SHA = "0" * 40
MAC_CONFIG = "macos/Hako/Version.xcconfig"
MAC_PROJECT = "macos/Hako/Hako.xcodeproj/project.pbxproj"
WINDOWS_MANIFEST = "windows/Hako/Package.appxmanifest"
WINDOWS_MANIFEST_NS = "http://schemas.microsoft.com/appx/manifest/foundation/windows10"


class GateError(RuntimeError):
    pass


def git(repo: Path, *args: str, check: bool = True) -> str:
    result = subprocess.run(
        ["git", *args], cwd=repo, text=True, encoding="utf-8", errors="replace",
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    if check and result.returncode:
        detail = result.stderr.strip() or f"git {' '.join(args)} exited {result.returncode}"
        raise GateError(detail)
    return result.stdout


def git_succeeds(repo: Path, *args: str) -> bool:
    return subprocess.run(
        ["git", *args], cwd=repo, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    ).returncode == 0


def ensure_commit(repo: Path, sha: str, label: str) -> None:
    if not re.fullmatch(r"[0-9a-fA-F]{40}", sha):
        raise GateError(f"The push {label} SHA is missing or invalid: {sha!r}.")
    if not git_succeeds(repo, "cat-file", "-e", f"{sha}^{{commit}}"):
        fetched = subprocess.run(
            ["git", "fetch", "--no-tags", "origin", sha], cwd=repo,
            text=True, encoding="utf-8", errors="replace",
            stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        )
        if fetched.returncode or not git_succeeds(repo, "cat-file", "-e", f"{sha}^{{commit}}"):
            detail = fetched.stderr.strip() or "the object is still unavailable after fetching"
            raise GateError(f"Cannot read push {label} commit {sha}: {detail}")


def read_file(repo: Path, sha: str, path: str) -> str | None:
    result = subprocess.run(
        ["git", "show", f"{sha}:{path}"], cwd=repo,
        text=True, encoding="utf-8", errors="replace",
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    return result.stdout if result.returncode == 0 else None


def parse_numeric_version(value: str, label: str) -> tuple[int, int, int]:
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*)){1,2}", value):
        raise GateError(f"Malformed {label} version {value!r}; expected numeric X.Y or X.Y.Z.")
    numbers = tuple(int(part) for part in value.split("."))
    return (numbers + (0,) * (3 - len(numbers)))[:3]


def mac_version(repo: Path, sha: str, label: str) -> tuple[tuple[int, int, int], str]:
    config = read_file(repo, sha, MAC_CONFIG)
    if config is not None:
        matches = re.findall(r"^\s*MARKETING_VERSION\s*=\s*([^\s#]+)\s*(?:#.*)?$", config, re.MULTILINE)
        if len(matches) != 1:
            raise GateError(f"{label} {MAC_CONFIG} must define MARKETING_VERSION exactly once.")
        raw = matches[0]
    else:
        project = read_file(repo, sha, MAC_PROJECT)
        if project is None:
            raise GateError(f"{label} has neither {MAC_CONFIG} nor {MAC_PROJECT}.")
        matches = re.findall(r"MARKETING_VERSION\s*=\s*([^;\s]+)\s*;", project)
        distinct = set(matches)
        if len(distinct) != 1:
            raise GateError(f"{label} legacy {MAC_PROJECT} has no single MARKETING_VERSION.")
        raw = matches[0]
    version = parse_numeric_version(raw, f"macOS {label}")
    return version, ".".join(str(number) for number in version)


def windows_version(repo: Path, sha: str, label: str) -> tuple[tuple[int, int, int], str]:
    manifest = read_file(repo, sha, WINDOWS_MANIFEST)
    if manifest is None:
        raise GateError(f"{label} has no {WINDOWS_MANIFEST}.")
    try:
        root = ET.fromstring(manifest)
    except ET.ParseError as error:
        raise GateError(f"Malformed {label} {WINDOWS_MANIFEST}: {error}") from error
    identity = root.find(f"{{{WINDOWS_MANIFEST_NS}}}Identity")
    raw = identity.attrib.get("Version", "") if identity is not None else ""
    if not re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.0", raw):
        raise GateError(f"Malformed Windows {label} version {raw!r}; expected numeric X.Y.Z.0.")
    version = tuple(int(part) for part in raw.split(".")[:3])
    return version, ".".join(str(number) for number in version)


def has_tree_path(repo: Path, sha: str, path: str) -> bool:
    return git_succeeds(repo, "cat-file", "-e", f"{sha}:{path}")


def changed_paths(repo: Path, before: str | None, after: str) -> set[str]:
    if before is None:
        output = git(repo, "ls-tree", "-r", "--name-only", "-z", after)
    else:
        output = git(repo, "diff", "--no-renames", "--name-only", "-z", before, after)
    return {path for path in output.split("\0") if path}


def detect(repo: Path, before: str, after: str) -> dict[str, str]:
    if not re.fullmatch(r"[0-9a-fA-F]{40}", before):
        raise GateError("The push event has no valid `before` SHA; refusing to substitute HEAD^.")
    ensure_commit(repo, after, "after")
    is_initial_push = before == ZERO_SHA
    if not is_initial_push:
        ensure_commit(repo, before, "before")
    paths = changed_paths(repo, None if is_initial_push else before, after)

    result: dict[str, str] = {}
    for name, directory, reader, prefix in (
        ("macos", "macos/", mac_version, "macos-v"),
        ("windows", "windows/", windows_version, "windows-v"),
    ):
        changed = any(path.startswith(directory) for path in paths)
        eligible = False
        version_text = ""
        tag = ""
        if changed:
            after_version, version_text = reader(repo, after, "after")
            if not is_initial_push and has_tree_path(repo, before, directory.rstrip("/")):
                before_version, _ = reader(repo, before, "before")
            else:
                before_version = (0, 0, 0)
            eligible = after_version > before_version
            tag = prefix + version_text
        result[name] = "true" if eligible else "false"
        result[f"{name}_version"] = version_text
        result[f"{name}_tag"] = tag
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--before", required=True)
    parser.add_argument("--after", default=os.environ.get("GITHUB_SHA"))
    parser.add_argument("--repository", default=".")
    args = parser.parse_args()
    if not args.after:
        parser.error("--after or GITHUB_SHA is required")
    try:
        outputs = detect(Path(args.repository).resolve(), args.before, args.after)
    except GateError as error:
        print(f"release gate: {error}", file=sys.stderr)
        return 1

    output_file = os.environ.get("GITHUB_OUTPUT")
    if output_file:
        with open(output_file, "a", encoding="utf-8", newline="\n") as stream:
            for key, value in outputs.items():
                stream.write(f"{key}={value}\n")
    else:
        print(json.dumps(outputs, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
