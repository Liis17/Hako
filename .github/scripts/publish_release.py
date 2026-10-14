#!/usr/bin/env python3
"""Create or resume a stable GitHub draft release, then publish it."""

from __future__ import annotations

import argparse
import itertools
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any, Callable


API_ROOT = "https://api.github.com"
UPLOAD_ROOT = "https://uploads.github.com"


class GitHubAPIError(RuntimeError):
    def __init__(self, status: int, message: str):
        super().__init__(f"HTTP {status}: {message}")
        self.status = status


class GitHubAPI:
    def __init__(self, repository: str, token: str):
        self.repository = repository
        self.token = token

    def request(
        self,
        method: str,
        url: str,
        body: Any = None,
        content_type: str = "application/vnd.github+json",
    ) -> Any:
        data = body if isinstance(body, bytes) else (
            json.dumps(body).encode("utf-8") if body is not None else None
        )
        request = urllib.request.Request(
            url,
            data=data,
            method=method,
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": f"Bearer {self.token}",
                "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "Hako-release-workflow",
                "Content-Type": content_type,
            },
        )
        try:
            with urllib.request.urlopen(request) as response:
                payload = response.read()
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace").strip()
            if detail:
                try:
                    detail = json.loads(detail).get("message", detail)
                except (json.JSONDecodeError, AttributeError):
                    pass
            raise GitHubAPIError(error.code, detail or error.reason) from error
        except urllib.error.URLError as error:
            raise RuntimeError(f"GitHub API request failed: {error.reason}") from error
        if not payload:
            return None
        return json.loads(payload)

    def release_by_tag(self, tag: str) -> dict[str, Any] | None:
        for page in itertools.count(1):
            releases = self.request(
                "GET", f"{API_ROOT}/repos/{self.repository}/releases?per_page=100&page={page}"
            )
            matching = [release for release in releases if release.get("tag_name") == tag]
            if len(matching) > 1:
                raise RuntimeError(f"More than one GitHub release uses tag {tag!r}.")
            if matching:
                return matching[0]
            if len(releases) < 100:
                return None

    def create_release(self, payload: dict[str, Any]) -> dict[str, Any]:
        return self.request("POST", f"{API_ROOT}/repos/{self.repository}/releases", payload)

    def update_release(self, release_id: int, payload: dict[str, Any]) -> dict[str, Any]:
        return self.request("PATCH", f"{API_ROOT}/repos/{self.repository}/releases/{release_id}", payload)

    def list_assets(self, release_id: int) -> list[dict[str, Any]]:
        return self.request(
            "GET", f"{API_ROOT}/repos/{self.repository}/releases/{release_id}/assets?per_page=100"
        )

    def delete_asset(self, release_id: int, asset_id: int) -> None:
        self.request("DELETE", f"{API_ROOT}/repos/{self.repository}/releases/assets/{asset_id}")

    def upload_asset(self, release_id: int, path: Path) -> None:
        query = urllib.parse.urlencode({"name": path.name})
        url = f"{UPLOAD_ROOT}/repos/{self.repository}/releases/{release_id}/assets?{query}"
        self.request("POST", url, path.read_bytes(), "application/octet-stream")


def parse_ls_remote_tag(tag: str, output: str) -> str | None:
    direct_ref = f"refs/tags/{tag}"
    peeled_ref = f"{direct_ref}^{{}}"
    direct: str | None = None
    peeled: str | None = None
    for line in output.splitlines():
        fields = line.split("\t", 1)
        if len(fields) != 2:
            continue
        sha, ref = fields
        if ref == peeled_ref:
            peeled = sha
        elif ref == direct_ref:
            direct = sha
    return peeled or direct


def resolve_remote_tag(tag: str) -> str | None:
    result = subprocess.run(
        ["git", "ls-remote", "--tags", "origin", f"refs/tags/{tag}", f"refs/tags/{tag}^{{}}"],
        text=True, encoding="utf-8", errors="replace",
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    if result.returncode:
        raise RuntimeError(f"Could not resolve remote tag {tag!r}: {result.stderr.strip()}")
    return parse_ls_remote_tag(tag, result.stdout)


def _release_mutation(api_call: Callable[[], dict[str, Any]], operation: str) -> dict[str, Any]:
    try:
        return api_call()
    except GitHubAPIError as error:
        if error.status in (403, 404):
            raise RuntimeError(
                f"GitHub rejected release {operation}: {error}. "
                "A historical source commit can require Workflows: write; GITHUB_TOKEN cannot grant it. "
                "The release was not treated as missing."
            ) from error
        raise RuntimeError(f"GitHub release {operation} failed: {error}") from error


class ReleasePublisher:
    def __init__(self, api: Any, tag_resolver: Callable[[str], str | None]):
        self.api = api
        self.tag_resolver = tag_resolver

    def publish(
        self,
        tag: str,
        source_sha: str,
        title: str,
        notes: str,
        asset_paths: list[Path],
    ) -> str:
        if not re.fullmatch(r"[0-9a-fA-F]{40}", source_sha):
            raise RuntimeError(f"Invalid source commit SHA: {source_sha!r}.")
        source_sha = source_sha.lower()
        if not asset_paths or any(not path.is_file() for path in asset_paths):
            missing = [str(path) for path in asset_paths if not path.is_file()]
            raise RuntimeError(f"Release assets are missing: {', '.join(missing) or 'no assets were supplied'}.")

        tag_sha = self.tag_resolver(tag)
        if tag_sha is not None and tag_sha.lower() != source_sha:
            raise RuntimeError(
                f"Tag {tag} resolves to {tag_sha}, but this run builds {source_sha}; refusing to overwrite it."
            )

        release = self.api.release_by_tag(tag)
        if release is not None:
            if tag_sha is None:
                raise RuntimeError(f"Release {tag} exists, but its actual Git tag cannot be resolved.")
            if not release.get("draft", False):
                return "already-published"

        details = {
            "tag_name": tag,
            "target_commitish": source_sha,
            "name": title,
            "body": notes,
            "draft": True,
            "prerelease": False,
        }
        if release is None:
            release = _release_mutation(lambda: self.api.create_release(details), "creation")
        else:
            release = _release_mutation(
                lambda: self.api.update_release(release["id"], details), "draft update"
            )

        release_id = release["id"]
        for asset in self.api.list_assets(release_id):
            self.api.delete_asset(release_id, asset["id"])
        for path in asset_paths:
            self.api.upload_asset(release_id, path)

        _release_mutation(
            lambda: self.api.update_release(release_id, {"draft": False, "target_commitish": source_sha}),
            "publication",
        )
        return "published"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--tag", required=True)
    parser.add_argument("--title", required=True)
    parser.add_argument("--notes", required=True)
    parser.add_argument("--asset", action="append", type=Path, required=True)
    args = parser.parse_args()

    repository = os.environ.get("GITHUB_REPOSITORY", "")
    token = os.environ.get("GITHUB_TOKEN", "")
    source_sha = os.environ.get("GITHUB_SHA", "")
    if not re.fullmatch(r"[^/]+/[^/]+", repository):
        parser.error("GITHUB_REPOSITORY must be owner/name")
    if not token:
        parser.error("GITHUB_TOKEN is required")
    try:
        outcome = ReleasePublisher(GitHubAPI(repository, token), resolve_remote_tag).publish(
            args.tag, source_sha, args.title, args.notes, args.asset
        )
    except (GitHubAPIError, RuntimeError) as error:
        print(f"release publisher: {error}", file=sys.stderr)
        return 1
    if outcome == "already-published":
        print(f"Release {args.tag} already points to this source commit; leaving it unchanged.")
    else:
        print(f"Published stable release {args.tag} from {source_sha}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
