#!/usr/bin/env python3
"""Bundle canonical operator guides while preserving the flat deployment archive."""

from pathlib import Path
import argparse
import shutil


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("destination", type=Path)
    parser.add_argument("--flat-deployment", action="store_true")
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[2]
    destination = args.destination
    destination.mkdir(parents=True, exist_ok=True)
    shutil.copytree(repository / "docs", destination / "docs", dirs_exist_ok=True)
    shutil.copytree(repository / "licenses", destination / "licenses", dirs_exist_ok=True)
    for name in ("AGENTS.md", "CONTRIBUTING.md", "SECURITY.md", ".env.example"):
        shutil.copy2(repository / name, destination / name)
    if args.flat_deployment:
        # Existing deployments extract Compose at archive root. Fix only bundled
        # local-link targets; source canonical guides remain repository-relative.
        for path in (destination / "docs").rglob("*.md"):
            path.write_text(path.read_text().replace("](../../docker/", "](../../"), encoding="utf-8")
        example = destination / "examples/mcp-http/README.md"
        example.write_text(example.read_text().replace("](../../../docs/", "](../../docs/"), encoding="utf-8")
    else:
        shutil.copytree(repository / "docker", destination / "docker", dirs_exist_ok=True)
        shutil.copytree(repository / "tools/mcp", destination / "tools/mcp", dirs_exist_ok=True,
                        ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))


if __name__ == "__main__":
    main()
