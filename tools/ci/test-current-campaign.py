#!/usr/bin/env python3
"""The current release adapter preserves all historical campaign requirements."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("current_release_campaign", Path(__file__).with_name("validate-current-campaign.py"))
adapter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(adapter)
gate = adapter.gate
CATALOG = ROOT / "docs/contracts/mcp-operation-catalog.json"


class CurrentCampaignTests(unittest.TestCase):
    def test_current_catalog_initializes_full_unrun_scope(self):
        with tempfile.TemporaryDirectory(prefix="rateldesk-current-campaign-") as directory:
            target = Path(directory) / "new-campaign"
            result = gate.initialize(target, CATALOG)
            self.assertEqual("NOT_RUN", result["readiness"])
            self.assertEqual(390, result["operations"])
            self.assertEqual(103, result["domainCases"])
            self.assertEqual(86, result["historicalObligations"])
            self.assertEqual(9, result["cleanupAssertions"])
            receipt = json.loads((target / "receipt.json").read_text(encoding="utf-8"))
            self.assertEqual("rateldesk-campaign/v1", receipt["format"])
            self.assertEqual(adapter.version, receipt["campaign"]["version"])

    def test_current_adapter_rejects_historical_revision(self):
        catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
        catalog["catalogRevision"] = "2026-10-25.beta11-contracts-v25"
        with tempfile.TemporaryDirectory(prefix="rateldesk-old-revision-") as directory:
            candidate = Path(directory) / "catalog.json"
            candidate.write_text(json.dumps(catalog), encoding="utf-8")
            with self.assertRaises(gate.Invalid):
                gate.export_catalog(candidate)


if __name__ == "__main__":
    unittest.main()
