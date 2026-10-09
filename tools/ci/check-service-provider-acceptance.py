#!/usr/bin/env python3
"""Reject absent, failed or skipped required receiver/service acceptance evidence."""

import re
import sys
import xml.etree.ElementTree as ET


def main(path: str) -> None:
    root = ET.parse(path).getroot()
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    results = root.findall("t:Results/t:UnitTestResult", ns)
    if not results or any(result.get("outcome") != "Passed" for result in results):
        raise SystemExit("Required provider acceptance must contain passing results and no failed/skipped cases.")
    counters = root.find("t:ResultSummary/t:Counters", ns)
    if counters is None or int(counters.get("total", "0")) != len(results) or int(counters.get("passed", "0")) != len(results):
        raise SystemExit("Required provider acceptance counters do not prove all selected cases passed.")
    classes = {
        "IncidentReceiverTests": True,
        "IncidentReceiverConformanceTests": False,
        "ServiceIdentityHttpTests": True,
        "ManagedOrchestrationCallbackHttpTests": True,
        "PairingServiceTests": True,
        "PairingEndpointTests": False,
        "PairingUpgradeTests": True,
        "ServiceIdentityMigrationTests": True,
    }
    for name, requires_providers in classes.items():
        names = [result.get("testName", "") for result in results if f".{name}." in result.get("testName", "")]
        if not names:
            raise SystemExit(f"Required acceptance class has no executed cases: {name}")
        if requires_providers:
            providers = {
                match.group(1).lower()
                for test_name in names
                if (match := re.search(r"\bpostgres:\s*(true|false)\b", test_name, re.IGNORECASE))
            }
            if providers != {"true", "false"}:
                raise SystemExit(f"Required acceptance class lacks actual SQLite and PostgreSQL cases: {name}")
        print(f"{name}: {len(names)} passed; skips=0")
    print(f"Required provider acceptance: {len(results)} passed; failures=0; skips=0")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: check-service-provider-acceptance.py <acceptance.trx>")
    main(sys.argv[1])
