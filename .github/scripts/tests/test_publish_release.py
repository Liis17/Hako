from __future__ import annotations

import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "publish_release.py"
SPEC = importlib.util.spec_from_file_location("publish_release", SCRIPT)
publish_release = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(publish_release)


class FakeAPI:
    def __init__(self, release: dict | None = None):
        self.release = release
        self.assets: list[dict] = []
        self.events: list[tuple] = []
        self.next_asset_id = 10

    def release_by_tag(self, tag: str) -> dict | None:
        self.events.append(("get", tag))
        return self.release

    def create_release(self, payload: dict) -> dict:
        self.events.append(("create", payload.copy()))
        self.release = {"id": 1, "draft": True, **payload}
        return self.release

    def update_release(self, release_id: int, payload: dict) -> dict:
        self.events.append(("update", payload.copy()))
        self.release.update(payload)
        return self.release

    def list_assets(self, release_id: int) -> list[dict]:
        self.events.append(("list-assets", release_id))
        return list(self.assets)

    def delete_asset(self, release_id: int, asset_id: int) -> None:
        self.events.append(("delete-asset", asset_id))
        self.assets = [asset for asset in self.assets if asset["id"] != asset_id]

    def upload_asset(self, release_id: int, path: Path) -> None:
        self.events.append(("upload", path.name))
        self.assets.append({"id": self.next_asset_id, "name": path.name})
        self.next_asset_id += 1


class ReleasePublisherTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        root = Path(self.temp.name)
        self.assets = [root / "Hako.dmg", root / "Hako.cer"]
        for path in self.assets:
            path.write_bytes(b"test asset")
        self.source = "a" * 40
        self.tag = "macos-v1.2.3"

    def tearDown(self) -> None:
        self.temp.cleanup()

    def publisher(self, api: FakeAPI, tag_sha: str | None = None):
        return publish_release.ReleasePublisher(api, lambda _: tag_sha)

    def test_published_same_sha_is_left_untouched(self) -> None:
        api = FakeAPI({"id": 1, "draft": False, "target_commitish": "b" * 40})
        result = self.publisher(api, self.source).publish(
            self.tag, self.source, "Hako 1.2.3", "source", self.assets
        )
        self.assertEqual(result, "already-published")
        self.assertEqual(api.events, [("get", self.tag)])

    def test_annotated_tag_is_peeled_and_draft_resumes_safely(self) -> None:
        tag_object = "c" * 40
        output = f"{tag_object}\trefs/tags/{self.tag}\n{self.source}\trefs/tags/{self.tag}^{{}}\n"
        self.assertEqual(publish_release.parse_ls_remote_tag(self.tag, output), self.source)
        api = FakeAPI({"id": 7, "draft": True, "target_commitish": "b" * 40})
        api.assets = [
            {"id": 1, "name": "Hako.dmg"},
            {"id": 2, "name": "old.txt"},
        ]
        result = self.publisher(api, publish_release.parse_ls_remote_tag(self.tag, output)).publish(
            self.tag, self.source, "Hako 1.2.3", "source", self.assets
        )
        self.assertEqual(result, "published")
        self.assertEqual(api.release["target_commitish"], self.source)
        self.assertFalse(api.release["draft"])
        self.assertEqual({asset["name"] for asset in api.assets}, {"Hako.dmg", "Hako.cer"})
        upload_positions = [i for i, event in enumerate(api.events) if event[0] == "upload"]
        publish_position = next(
            i for i, event in enumerate(api.events)
            if event[0] == "update" and event[1].get("draft") is False
        )
        self.assertEqual(len(upload_positions), 2)
        self.assertTrue(all(position < publish_position for position in upload_positions))

    def test_conflicting_actual_tag_wins_over_lying_release_metadata(self) -> None:
        other_sha = "d" * 40
        tag_object = "c" * 40
        output = f"{tag_object}\trefs/tags/{self.tag}\n{other_sha}\trefs/tags/{self.tag}^{{}}\n"
        api = FakeAPI({"id": 1, "draft": True, "target_commitish": self.source})
        with self.assertRaisesRegex(RuntimeError, "refusing to overwrite"):
            self.publisher(api, publish_release.parse_ls_remote_tag(self.tag, output)).publish(
                self.tag, self.source, "Hako 1.2.3", "source", self.assets
            )
        self.assertEqual(api.events, [])

    def test_new_release_stays_draft_until_every_asset_is_uploaded(self) -> None:
        api = FakeAPI()
        result = self.publisher(api).publish(
            self.tag, self.source, "Hako 1.2.3", "source", self.assets
        )
        self.assertEqual(result, "published")
        create = next(event[1] for event in api.events if event[0] == "create")
        self.assertTrue(create["draft"])
        self.assertEqual(create["target_commitish"], self.source)
        self.assertFalse(api.release["draft"])
        self.assertFalse(api.release["prerelease"])

    def test_macos_publishes_both_dmg_names_before_leaving_draft(self) -> None:
        versioned = Path(self.temp.name) / "Hako-1.2.3-macos.dmg"
        legacy = Path(self.temp.name) / "Hako.dmg"
        versioned.write_bytes(legacy.read_bytes())

        class InspectUploads(FakeAPI):
            def upload_asset(inner, release_id: int, path: Path) -> None:
                self.assertTrue(inner.release["draft"])
                self.assertEqual(path.read_bytes(), versioned.read_bytes())
                super().upload_asset(release_id, path)

        api = InspectUploads()
        result = self.publisher(api).publish(
            self.tag, self.source, "Hako 1.2.3", "source", [versioned, legacy]
        )
        self.assertEqual(result, "published")
        self.assertEqual(
            {asset["name"] for asset in api.assets},
            {"Hako-1.2.3-macos.dmg", "Hako.dmg"},
        )
        self.assertFalse(api.release["draft"])
        self.assertEqual(api.events[-1][0], "update")
        self.assertIs(api.events[-1][1]["draft"], False)

    def test_prerelease_flag_is_published(self) -> None:
        api = FakeAPI()
        result = self.publisher(api).publish(
            "main-" + self.source, self.source, "Hako main build", "source", self.assets, prerelease=True
        )
        self.assertEqual(result, "published")
        create = next(event[1] for event in api.events if event[0] == "create")
        publication = next(
            event[1] for event in api.events if event[0] == "update" and event[1].get("draft") is False
        )
        self.assertTrue(create["prerelease"])
        self.assertTrue(publication["prerelease"])

    def test_historical_create_404_is_a_clear_error_not_missing_release(self) -> None:
        class Create404(FakeAPI):
            def create_release(self, payload: dict) -> dict:
                raise publish_release.GitHubAPIError(404, "Not Found")

        api = Create404()
        with self.assertRaisesRegex(RuntimeError, "HTTP 404.*Workflows: write"):
            self.publisher(api).publish(
                self.tag, self.source, "Hako 1.2.3", "source", self.assets
            )


if __name__ == "__main__":
    unittest.main()
