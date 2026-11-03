#!/usr/bin/env python3
"""Bundle canonical operator guides while preserving the flat deployment archive."""

from pathlib import Path
import argparse
import shutil


def package_client_helpers(repository: Path, destination: Path) -> None:
    helpers = repository / "tools/mcp"
    # Keep one complete implementation beside the native executable. Its
    # __file__-relative imports, release metadata and resume paths stay intact.
    shutil.copytree(helpers, destination, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))
    aliases = destination / "tools/mcp"
    aliases.mkdir(parents=True, exist_ok=True)
    for source in helpers.glob("*.py"):
        alias = aliases / source.name
        # Preserve imported-module globals as well as command-line execution.
        # The root implementation never points back to these forwarding files.
        alias.write_text(
            '#!/usr/bin/env python3\n'
            '"""Load the archive-root helper and its colocated dependencies."""\n'
            'from pathlib import Path as _Path\n'
            f'__file__ = str(_Path(__file__).resolve().parents[2] / {source.name!r})\n'
            'exec(compile(_Path(__file__).read_bytes(), __file__, "exec"), globals())\n',
            encoding="utf-8")
        shutil.copymode(source, alias)
    for source in helpers.glob("*.sh"):
        shutil.copy2(source, aliases / source.name)


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
    # AGENTS.md and release notes refer to this portable, MIT-licensed index.
    # Retain the complete references and upstream license in offline bundles.
    shutil.copytree(repository / ".agents/skills", destination / ".agents/skills", dirs_exist_ok=True)
    if args.flat_deployment:
        # Existing deployments extract Compose at archive root. Fix only bundled
        # local-link targets; source canonical guides remain repository-relative.
        for path in (destination / "docs").rglob("*.md"):
            path.write_text(path.read_text().replace("](../../docker/", "](../../"), encoding="utf-8")
        example = destination / "examples/mcp-http/README.md"
        example.write_text(example.read_text().replace("](../../../docs/", "](../../docs/"), encoding="utf-8")
    else:
        shutil.copytree(repository / "docker", destination / "docker", dirs_exist_ok=True)
        package_client_helpers(repository, destination)


if __name__ == "__main__":
    main()
