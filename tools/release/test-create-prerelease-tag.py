#!/usr/bin/env python3
"""Offline failure tests for prerelease source/CI/immutability checks."""

import copy
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("create_prerelease_tag", Path(__file__).with_name("create-prerelease-tag.py"))
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)
SOURCE = "a" * 40
VERSION = "1.2.3-beta.8"
ENV = {
    "RELEASE_TAG": "v" + VERSION, "RELEASE_SOURCE_REVISION": SOURCE,
    "GITHUB_REPOSITORY": "example/project", "GITHUB_REF": "refs/heads/main",
    "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_SHA": SOURCE, "GH_TOKEN": "synthetic-workflow-token",
}


def evidence():
    run = {"id": 42, "head_sha": SOURCE, "head_branch": "main", "event": "push", "status": "completed", "conclusion": "success"}
    jobs = [{"name": name, "run_id": 42, "head_sha": SOURCE, "status": "completed", "conclusion": "success"} for name in MODULE.REQUIRED_JOBS]
    return [run], jobs


class TagPreparationTests(unittest.TestCase):
    def test_canonical_and_legacy_beta_and_rc_tags_match_exact_repository_version(self):
        for version in (VERSION, "1.2.3-rc.1"):
            for prefix in ("v", "RatelDesk-"):
                with self.subTest(version=version, prefix=prefix):
                    self.assertEqual(prefix + version, MODULE.validate_request({**ENV, "RELEASE_TAG": prefix + version}, version)[0])

    def test_stable_other_suffix_and_version_override_are_rejected(self):
        for tag, version in (("v1.2.3", "1.2.3"), ("v1.2.3-alpha.1", "1.2.3-alpha.1"), ("v1.2.3-beta.08", "1.2.3-beta.08"), ("v" + VERSION, "1.2.3-beta.7")):
            with self.subTest(tag=tag), self.assertRaises(SystemExit):
                MODULE.validate_request({**ENV, "RELEASE_TAG": tag}, version)

    def test_non_main_non_dispatch_short_or_different_source_is_rejected(self):
        for field, value in (("GITHUB_REF", "refs/heads/feature"), ("GITHUB_EVENT_NAME", "push"),
                             ("RELEASE_SOURCE_REVISION", "a" * 7), ("GITHUB_SHA", "b" * 40),
                             ("GITHUB_REPOSITORY", "../other/repository")):
            with self.subTest(field=field), self.assertRaises(SystemExit):
                MODULE.validate_request({**ENV, field: value}, VERSION)

    def test_complete_exact_main_ci_is_accepted(self):
        runs, jobs = evidence()
        self.assertEqual(42, MODULE.validate_main_ci(SOURCE, runs, jobs)["id"])

    def test_missing_skipped_failed_or_duplicate_required_job_is_rejected(self):
        for missing in MODULE.REQUIRED_JOBS:
            runs, jobs = evidence()
            for mode in ("absent", "skipped", "failure", "duplicate"):
                changed = copy.deepcopy(jobs)
                target = next(job for job in changed if job["name"] == missing)
                if mode == "absent":
                    changed.remove(target)
                elif mode == "duplicate":
                    changed.append(copy.deepcopy(target))
                else:
                    target["conclusion"] = mode
                with self.subTest(job=missing, mode=mode), self.assertRaises(SystemExit):
                    MODULE.validate_main_ci(SOURCE, runs, changed)

    def test_latest_ci_run_must_be_complete_even_when_an_older_run_passed(self):
        runs, jobs = evidence()
        runs.append({**runs[0], "id": 43, "status": "in_progress", "conclusion": None})
        with self.assertRaises(SystemExit):
            MODULE.validate_main_ci(SOURCE, runs, jobs)

    def test_different_branch_event_source_and_job_run_are_rejected(self):
        for field, value in (("head_branch", "feature"), ("event", "pull_request"), ("head_sha", "b" * 40)):
            runs, jobs = evidence()
            runs[0][field] = value
            with self.subTest(field=field), self.assertRaises(SystemExit):
                MODULE.validate_main_ci(SOURCE, runs, jobs)
        for field, value in (("head_sha", "b" * 40), ("run_id", 41)):
            runs, jobs = evidence()
            jobs[0][field] = value
            with self.subTest(field=field), self.assertRaises(SystemExit):
                MODULE.validate_main_ci(SOURCE, runs, jobs)

    def test_existing_image_refuses_version_allocation(self):
        with patch.object(MODULE, "request_json", side_effect=[{"token": "synthetic-registry-token"}, {"schemaVersion": 2}]):
            with self.assertRaisesRegex(SystemExit, "already has a registry manifest"):
                MODULE.require_unused_images("example/project", VERSION)

    def test_registry_unavailable_is_not_treated_as_unused(self):
        with patch.object(MODULE, "request_json", side_effect=SystemExit("registry unavailable")):
            with self.assertRaises(SystemExit):
                MODULE.require_unused_images("example/project", VERSION)

    def test_preflight_creates_only_one_new_ref_after_all_checks_and_never_on_failure(self):
        runs, jobs = evidence()
        for failure in ("main_moved", "tag_exists", "draft_exists", "late_main_move", None):
            writes = []
            main_reads = 0

            def api(url, headers=None, body=None, allow_missing=False):
                nonlocal main_reads
                if body is not None:
                    writes.append(body)
                    return {"ref": body["ref"], "object": {"sha": body["sha"]}}
                if url.endswith("/git/ref/heads/main"):
                    main_reads += 1
                    moved = failure == "main_moved" or failure == "late_main_move" and main_reads > 1
                    return {"object": {"sha": "b" * 40 if moved else SOURCE}}
                if "/workflows/" in url:
                    return {"workflow_runs": runs}
                if "/jobs?" in url:
                    return {"jobs": jobs}
                if "/git/ref/tags/" in url:
                    return {"object": {"sha": SOURCE}} if failure == "tag_exists" else None
                if "/releases?" in url:
                    return [{"tag_name": "RatelDesk-" + VERSION, "draft": True}] if failure == "draft_exists" else []
                raise AssertionError(url)

            with self.subTest(failure=failure), patch.dict(MODULE.os.environ, ENV, clear=True), \
                    patch.object(MODULE.subprocess, "run") as process, patch.object(MODULE, "request_json", side_effect=api), \
                    patch.object(MODULE, "require_unused_images"):
                process.return_value.stdout = VERSION
                if failure is None:
                    with patch("builtins.print"):
                        MODULE.main()
                    self.assertEqual([{"ref": "refs/tags/v" + VERSION, "sha": SOURCE}], writes)
                else:
                    with self.assertRaises(SystemExit):
                        MODULE.main()
                    self.assertEqual([], writes)


if __name__ == "__main__":
    unittest.main()
