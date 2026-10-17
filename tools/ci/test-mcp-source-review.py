#!/usr/bin/env python3
"""Bounded source adapter tests. Real owner-tool rendering runs in host acceptance."""
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("rateldesk_source_review", root / "tools/mcp/source_review.py")
review = importlib.util.module_from_spec(spec)
spec.loader.exec_module(review)


class SourceReviewTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="rateldesk-source-review-")
        self.directory = Path(self.temporary.name)
        os.chmod(self.directory, 0o700)
        self.output = self.directory / "private"
        self.output.mkdir(mode=0o700)
        self.source = self.directory / "compose.yml"
        self.snapshot = self.output / "effective.json"
        self.sources = [self.source]
        self.renders = []

    def tearDown(self):
        self.temporary.cleanup()

    def render(self, sources):
        # Explicit adapter fixture, not a claim to implement full Compose YAML.
        self.renders.append(list(sources))
        result = {"services": {}}
        for source in sources:
            document = review.yaml.safe_load(Path(source).read_bytes())
            for service, fields in document["services"].items():
                effective = result["services"].setdefault(service, {})
                for key, value in fields.items():
                    if key == "environment":
                        if isinstance(value, list):
                            value = dict(item.split("=", 1) for item in value)
                        value = {key: content.replace("$$", "$") if isinstance(content, str) else str(content) if isinstance(content, (int, float)) else content for key, content in value.items()}
                        effective.setdefault(key, {}).update(value)
                    else:
                        effective[key] = value
        return result

    def context(self):
        snapshot = self.render([str(path) for path in self.sources])
        snapshot["x-rateldesk-source-files"] = [{"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()} for path in self.sources]
        snapshot["x-rateldesk-project-name"] = "synthetic-project"
        self.snapshot.write_text(json.dumps(snapshot))
        self.snapshot.chmod(0o600)
        self.renders.clear()
        return {"schemaVersion": 1, "sources": [str(path) for path in self.sources], "outputDirectory": str(self.output),
                "effectiveSnapshot": str(self.snapshot), "manager": "external-compose", "ownerComposeRequired": True}

    @staticmethod
    def overlay(**environment):
        return {"services": {"web": {"environment": environment}}}

    def test_mapping_scalar_edit_preserves_every_other_byte_and_all_four_services(self):
        content = (b'\xef\xbb\xbf# owner-managed\r\nservices:\r\n  api: {image: synthetic-api}\r\n  web:\r\n'
                   b'    image: synthetic-web # retain\r\n    environment:\r\n      TIMEOUT: "20" # retain comment\r\n'
                   b'      PRIVATE: "synthetic-hidden"\r\n    labels: {owner: original}\r\n'
                   b'  mcp-http: {image: synthetic-mcp}\r\n  db: {image: synthetic-db}\r\n')
        self.source.write_bytes(content)
        result = review.prepare_review(self.context(), self.overlay(TIMEOUT="21"), self.render)
        candidate = Path(result["sourceCandidates"][0]["candidate"])
        self.assertEqual(content.replace(b'TIMEOUT: "20"', b'TIMEOUT: "21"'), candidate.read_bytes())
        self.assertEqual(content, self.source.read_bytes())
        self.assertEqual(4, len(result["completeDeployment"]["services"]))
        self.assertEqual(0o600, candidate.stat().st_mode & 0o777)
        self.assertEqual(hashlib.sha256(content).hexdigest(), result["sourceCandidates"][0]["originalSha256"])
        safe_diff = result["sourceCandidates"][0]["safeDiff"]
        self.assertNotIn("synthetic-hidden", safe_diff)
        self.assertIn("-      TIMEOUT: [previous value redacted]", safe_diff)
        self.assertIn("+      TIMEOUT: [reviewed value]", safe_diff)
        self.assertNotIn("PRIVATE", safe_diff)
        self.assertEqual(2, len(self.renders))

    def test_list_edit_and_append_keep_literal_dollars_comments_and_unrelated_values(self):
        content = 'services:\n  web:\n    image: synthetic-web\n    environment:\n      - "TIMEOUT=20" # retain\n      - "UNCHANGED=yes"\n  db:\n    image: synthetic-db\n'
        self.source.write_text(content)
        result = review.prepare_review(self.context(), self.overlay(TIMEOUT="21", LITERAL="${synthetic}"), self.render)
        candidate = Path(result["sourceCandidates"][0]["candidate"]).read_text()
        self.assertIn('- "TIMEOUT=21" # retain', candidate)
        self.assertIn('- "UNCHANGED=yes"', candidate)
        self.assertIn('- "LITERAL=$${synthetic}"', candidate)
        self.assertEqual("${synthetic}", result["completeDeployment"]["services"]["web"]["environment"]["LITERAL"])
        self.assertTrue(candidate.endswith('  db:\n    image: synthetic-db\n'))

    def test_production_owner_renderer_decodes_literal_dollars_once_for_original_and_candidate(self):
        content = (b'# readable owner source\nservices:\n  web:\n    image: synthetic-web\n'
            b'    environment:\n      LITERAL: "$${OLD}/$$$$/single$$" # retain\n'
            b'    command: ["echo", "$$COMMAND/$$$$"]\n'
            b'    entrypoint: ["echo", "$$ENTRYPOINT/$$$$"]\n'
            b'    healthcheck:\n      test: ["CMD-SHELL", "echo $$HEALTH; echo $$$$"]\n'
            b'  db:\n    image: synthetic-db\n    environment:\n      UNCHANGED: "$$$$"\n')
        self.source.write_bytes(content)
        raw_original = {"services": {"web": {"image": "synthetic-web",
            "environment": {"LITERAL": "$${OLD}/$$$$/single$$"}, "command": ["echo", "$$COMMAND/$$$$"],
            "entrypoint": ["echo", "$$ENTRYPOINT/$$$$"],
            "healthcheck": {"test": ["CMD-SHELL", "echo $$HEALTH; echo $$$$"]}},
            "db": {"image": "synthetic-db", "environment": {"UNCHANGED": "$$$$"}}}}
        raw_candidate = copy.deepcopy(raw_original)
        raw_candidate["services"]["web"]["environment"]["LITERAL"] = "$${NEW}/$$$$/single$$"
        semantic = {"services": {"web": {"image": "synthetic-web",
            "environment": {"LITERAL": "${OLD}/$$/single$"}, "command": ["echo", "$COMMAND/$$"],
            "entrypoint": ["echo", "$ENTRYPOINT/$$"],
            "healthcheck": {"test": ["CMD-SHELL", "echo $HEALTH; echo $$"]}},
            "db": {"image": "synthetic-db", "environment": {"UNCHANGED": "$$"}}},
            "x-rateldesk-source-files": [{"path": str(self.source), "sha256": hashlib.sha256(content).hexdigest()}],
            "x-rateldesk-image-environment": {"web": ["IMAGE=$$UNCHANGED"]}}
        self.snapshot.write_text(json.dumps(semantic))
        self.snapshot.chmod(0o600)
        selection = {"DOCKER_CONFIG": str(self.directory / "original Docker config"), "DOCKER_CONTEXT": "synthetic-context"}
        context = {"schemaVersion": 1, "sources": [str(self.source)], "outputDirectory": str(self.output),
            "effectiveSnapshot": str(self.snapshot), "manager": "external-compose", "ownerComposeRequired": True,
            "compose": ["compose", "--project-directory", str(self.directory), "-f", str(self.source)],
            "dockerSelection": selection, "daemonIdentity": "synthetic-daemon"}
        # Keep production default_renderer and its normalization boundary. Only
        # replace the imported Docker factory; no Docker process runs here.
        launcher_spec = importlib.util.spec_from_file_location("source_test_launcher", root / "tools/mcp/setup-http-mcp.py")
        launcher = importlib.util.module_from_spec(launcher_spec)
        launcher_spec.loader.exec_module(launcher)
        calls, bindings = [], []
        class OwnerDocker:
            def __init__(self):
                self.selection, self.daemon_identity = {}, None
            def bind_daemon(self):
                bindings.append((dict(self.selection), self.daemon_identity))
            def run(self, arguments, *, owner_tool=False):
                calls.append((list(arguments), owner_tool))
                source = arguments[arguments.index("-f") + 1]
                return json.dumps(raw_original if source == str(self_source) else raw_candidate).encode()
        self_source = self.source
        imported = SimpleNamespace(Docker=OwnerDocker, compose_toolchain=launcher.compose_toolchain,
            compose_runtime_model=launcher.compose_runtime_model, Prerequisite=launcher.Prerequisite)
        loader_spec = SimpleNamespace(loader=SimpleNamespace(exec_module=lambda module: None))
        with patch.object(review.importlib.util, "spec_from_file_location", return_value=loader_spec), \
                patch.object(review.importlib.util, "module_from_spec", return_value=imported):
            result = review.prepare_review(context, self.overlay(LITERAL="${NEW}/$$/single$"))
        complete = result["completeDeployment"]
        expected = copy.deepcopy(semantic)
        expected["services"]["web"]["environment"]["LITERAL"] = "${NEW}/$$/single$"
        self.assertEqual(expected, complete)
        self.assertEqual(content, self.source.read_bytes())
        candidate = Path(result["sourceCandidates"][0]["candidate"])
        self.assertEqual(content.replace(b'LITERAL: "$${OLD}/$$$$/single$$"', b'LITERAL: "$${NEW}/$$$$/single$$"'), candidate.read_bytes())
        self.assertEqual(2, len(calls))
        self.assertEqual([str(self.source), str(candidate)], [arguments[arguments.index("-f") + 1] for arguments, _ in calls])
        self.assertTrue(all(owner_tool and arguments[-3:] == ["config", "--format", "json"] for arguments, owner_tool in calls))
        self.assertTrue(bindings and all(value == (selection, "synthetic-daemon") for value in bindings))
        self.assertEqual("$$$$", raw_original["services"]["db"]["environment"]["UNCHANGED"])

    def test_requested_yaml_scalar_changes_retain_single_quoted_and_plain_styles(self):
        content = "services:\n  web:\n    environment:\n      TIMEOUT: '20' # retained\n      ORIGIN: https://old.example.test\n      COUNT: 20\n  db:\n    image: synthetic-db\n"
        self.source.write_text(content)
        result = review.prepare_review(self.context(), self.overlay(TIMEOUT="21", ORIGIN="https://new.example.test", COUNT="21"), self.render)
        candidate = Path(result["sourceCandidates"][0]["candidate"]).read_text()
        expected = content.replace("TIMEOUT: '20'", "TIMEOUT: '21'").replace("https://old.example.test", "https://new.example.test").replace("COUNT: 20", "COUNT: 21")
        self.assertEqual(expected, candidate)

    def test_existing_json_scalar_replacement_preserves_original_bytes_and_extension(self):
        self.source = self.directory / "compose.json"
        self.sources = [self.source]
        content = b'\xef\xbb\xbf' + ('{\r\n  "services": {\r\n    "web": {"image":"synthetic-web", "environment": {"TIMEOUT": "20", "UNCHANGED": "original"}},\r\n'
                   '    "db": {"image": "synthetic-db"}\r\n  }, "x-owner": "preserve unicode é"\r\n}\r\n').encode("utf-8")
        self.source.write_bytes(content)
        result = review.prepare_review(self.context(), self.overlay(TIMEOUT="21"), self.render)
        candidate = Path(result["sourceCandidates"][0]["candidate"])
        self.assertEqual(".json", candidate.suffix)
        self.assertEqual(content.replace(b'"TIMEOUT": "20"', b'"TIMEOUT": "21"'), candidate.read_bytes())
        self.assertEqual(content, self.source.read_bytes())
        self.assertEqual("21", json.loads(candidate.read_bytes())["services"]["web"]["environment"]["TIMEOUT"])

    def test_json_environment_key_insertions_preserve_existing_source_spans(self):
        for environment in ('{"TIMEOUT":"20"}', '{\n        "TIMEOUT": "20"\n      }', '{}', '{\n      }'):
            with self.subTest(environment=environment):
                self.source = self.directory / "compose.json"
                self.sources = [self.source]
                content = '{ "services": { "web": {"image":"synthetic-web", "environment": ' + environment + '}, "db":{"image":"synthetic-db"}}, "x-owner": "unchanged" }\n'
                self.source.write_text(content)
                result = review.prepare_review(self.context(), self.overlay(LITERAL='${synthetic} "quoted"'), self.render)
                candidate = Path(result["sourceCandidates"][0]["candidate"]).read_text()
                parsed = json.loads(candidate)
                self.assertEqual('$${synthetic} "quoted"', parsed["services"]["web"]["environment"]["LITERAL"])
                self.assertEqual('${synthetic} "quoted"', result["completeDeployment"]["services"]["web"]["environment"]["LITERAL"])
                self.assertIn('"db":{"image":"synthetic-db"}}, "x-owner": "unchanged"', candidate)
                self.assertEqual(content, self.source.read_text())

    def test_json_environment_list_replacement_and_insertion_retain_json_shape(self):
        self.source = self.directory / "compose.json"
        self.sources = [self.source]
        content = '{"services":{"web":{"image":"synthetic-web","environment":["TIMEOUT=20", "UNCHANGED=yes"]},"db":{"image":"synthetic-db"}}}\n'
        self.source.write_text(content)
        result = review.prepare_review(self.context(), self.overlay(TIMEOUT="21", ADDED="safe"), self.render)
        candidate = Path(result["sourceCandidates"][0]["candidate"]).read_text()
        self.assertEqual(content.replace('"TIMEOUT=20"', '"TIMEOUT=21"').replace('"UNCHANGED=yes"]', '"UNCHANGED=yes", "ADDED=safe"]'), candidate)
        self.assertEqual(["TIMEOUT=21", "UNCHANGED=yes", "ADDED=safe"], json.loads(candidate)["services"]["web"]["environment"])

    def test_json_missing_environment_inserts_one_member_without_reserializing_service(self):
        self.source = self.directory / "compose.json"
        self.sources = [self.source]
        content = '{\n "services": {\n   "web": {\n     "image": "synthetic-web"\n   },\n   "db":{"image":"synthetic-db"}\n }, "x-owner":"keep"\n}\n'
        self.source.write_text(content)
        result = review.prepare_review(self.context(), self.overlay(TIMEOUT="21"), self.render)
        candidate = Path(result["sourceCandidates"][0]["candidate"]).read_text()
        expected = content.replace('"image": "synthetic-web"', '"image": "synthetic-web",\n     "environment": {"TIMEOUT": "21"}')
        self.assertEqual(expected, candidate)
        self.assertEqual("21", json.loads(candidate)["services"]["web"]["environment"]["TIMEOUT"])

    def test_missing_environment_adds_only_one_block_without_reformatting_service(self):
        content = '# saved source\nservices:\n  web:\n    image: synthetic-web\n    labels:\n      owner: original\n  db:\n    image: synthetic-db\n'
        self.source.write_text(content)
        result = review.prepare_review(self.context(), self.overlay(TIMEOUT="21"), self.render)
        expected = content.replace('  db:\n', '    environment:\n      TIMEOUT: "21"\n  db:\n')
        self.assertEqual(expected, Path(result["sourceCandidates"][0]["candidate"]).read_text())

    def test_ordered_sources_edit_actual_winning_key_and_leave_other_source_unchanged(self):
        self.source.write_text('services:\n  web:\n    image: synthetic-web\n    environment:\n      TIMEOUT: "10"\n      BASE: "original"\n')
        override = self.directory / "override.yml"
        override.write_text('services:\n  web:\n    environment:\n      TIMEOUT: "20" # winning owner\n')
        self.sources.append(override)
        before = self.source.read_bytes()
        context = self.context()
        result = review.prepare_review(context, self.overlay(TIMEOUT="21"), self.render)
        self.assertEqual([str(override)], [item["source"] for item in result["sourceCandidates"]])
        self.assertEqual(before, self.source.read_bytes())
        self.assertIn('TIMEOUT: "21" # winning owner', Path(result["sourceCandidates"][0]["candidate"]).read_text())
        again = review.prepare_review(context, self.overlay(TIMEOUT="21"), self.render)
        self.assertEqual(result, again)

    def test_source_dependency_and_render_window_changes_are_fenced(self):
        self.source.write_text('services:\n  web:\n    environment:\n      TIMEOUT: "20"\n')
        context = self.context()
        self.source.write_text(self.source.read_text() + '# owner changed\n')
        with self.assertRaisesRegex(review.SourceReviewError, "source_review_changed"):
            review.prepare_review(context, self.overlay(TIMEOUT="21"), self.render)
        context = self.context()
        def changed_during_render(sources):
            result = self.render(sources)
            if len(self.renders) == 2:
                self.source.write_text(self.source.read_text() + '# changed during render\n')
            return result
        with self.assertRaisesRegex(review.SourceReviewError, "source_review_changed"):
            review.prepare_review(context, self.overlay(TIMEOUT="21"), changed_during_render)

    def test_unintended_candidate_impact_never_returns_a_review(self):
        self.source.write_text('services:\n  web:\n    image: synthetic-web\n    environment:\n      TIMEOUT: "20"\n')
        def unexpected(sources):
            result = self.render(sources)
            if sources != [str(self.source)]:
                result["services"]["web"]["image"] = "unreviewed-image"
            return result
        with self.assertRaisesRegex(review.SourceReviewError, "source_review_impact"):
            review.prepare_review(self.context(), self.overlay(TIMEOUT="21"), unexpected)

    def test_unsupported_shapes_fail_without_source_reformatting(self):
        samples = [
            'services:\n  web:\n    environment: {TIMEOUT: "20"}\n',
            'services:\n  web:\n    environment: &shared\n      TIMEOUT: "20"\n',
            'services:\n  web:\n    environment:\n      TIMEOUT: "20"\n      TIMEOUT: "30"\n',
            'services:\n  web:\n    environment:\n      <<: {TIMEOUT: "20"}\n',
        ]
        for content in samples:
            with self.subTest(content=content):
                self.source.write_text(content)
                with self.assertRaises(review.SourceReviewError):
                    review.prepare_review(self.context(), self.overlay(TIMEOUT="21"), self.render)
                self.assertEqual(content, self.source.read_text())

    def test_unrelated_label_alias_and_literal_block_are_preserved(self):
        content = ('# readable owner source\nx-labels: &operator-labels\n  owner: original\n'
                   'x-notes: |\n  Preserve this block verbatim.\nservices:\n  web:\n    image: synthetic-web\n'
                   '    environment:\n      TIMEOUT: "20"\n    labels: *operator-labels\n')
        self.source.write_text(content)
        result = review.prepare_review(self.context(), self.overlay(TIMEOUT="21"), self.render)
        self.assertEqual(content.replace('TIMEOUT: "20"', 'TIMEOUT: "21"'), Path(result["sourceCandidates"][0]["candidate"]).read_text())
        self.assertEqual(content, self.source.read_text())

    def test_topology_overlay_is_rejected_before_any_render_or_candidate(self):
        self.source.write_text('services:\n  web:\n    image: synthetic-web\n')
        context = self.context()
        with self.assertRaisesRegex(review.SourceReviewError, "source_review_overlay"):
            review.prepare_review(context, {"services": {"helper": {"image": "synthetic-helper"}}}, self.render)
        self.assertEqual([], self.renders)
        self.assertEqual([], list(self.output.glob("source-review-*")))


if __name__ == "__main__":
    unittest.main(verbosity=2)
