#!/usr/bin/env python3
"""Validate the current release's laptop campaign without modifying historical gates."""
import importlib.util
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
properties = ET.parse(ROOT / "Directory.Build.props").getroot()
prefix = properties.findtext(".//VersionPrefix")
suffix = properties.findtext(".//VersionSuffix")
version = prefix + ("-" + suffix if suffix else "")
spec = importlib.util.spec_from_file_location("current_campaign_gate", Path(__file__).with_name("validate-beta12-campaign.py"))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
for module in (gate, gate.inherited, gate.legacy):
    module.VERSION = version
    module.FORMAT = "rateldesk-campaign/v1"
gate.PREREQUISITE_FORMAT = "rateldesk-prerequisites/v1"

if __name__ == "__main__":
    raise SystemExit(gate.main())
