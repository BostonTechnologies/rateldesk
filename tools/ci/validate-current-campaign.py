#!/usr/bin/env python3
"""Validate the current release's laptop campaign without modifying historical gates."""
import importlib.util
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
properties = ET.parse(ROOT / "Directory.Build.props").getroot()
prefix = properties.findtext(".//VersionPrefix")
suffix = properties.findtext(".//VersionSuffix")
version = prefix + ("-" + suffix if suffix else "")
current_catalog = json.loads(
    (ROOT / "docs/contracts/mcp-operation-catalog.json").read_text(encoding="utf-8")
)
spec = importlib.util.spec_from_file_location("current_campaign_gate", Path(__file__).with_name("validate-beta12-campaign.py"))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
for module in (gate, gate.inherited, gate.legacy):
    module.VERSION = version
    module.FORMAT = "rateldesk-campaign/v1"
    module.CATALOG_REVISION = current_catalog["catalogRevision"]
gate.PREREQUISITE_FORMAT = "rateldesk-prerequisites/v1"


def export_catalog(path):
    """Require the current generated catalog and every historical operation."""
    inherited = gate.inherited
    catalog, operations = inherited._export_catalog(path)
    gate.require(gate.legacy.sha256(inherited.BASELINE) == inherited.BASELINE_HASH,
                 "fixed beta.10 operation denominator changed")
    historical_ids = gate.read_json(inherited.BASELINE)["operationIds"]
    gate.require(len(historical_ids) == len(set(historical_ids)) == 389,
                 "historical operation denominator is incomplete")
    required = set(historical_ids) | {"helpdesk_email_settings.delete_rule"}
    gate.require(required <= set(operations), "current catalog removed a historical operation")
    gate.require(all(catalog[key] == current_catalog[key] for key in ("catalogHash", "schemaHash", "operationCount")),
                 "catalog must match the current release's generated operations and schemas")
    return catalog, operations


for module in (gate, gate.inherited, gate.legacy):
    module.export_catalog = export_catalog

if __name__ == "__main__":
    raise SystemExit(gate.main())
