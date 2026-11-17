#!/usr/bin/env python3
"""Bundle canonical operator guides while preserving the flat deployment archive."""

from pathlib import Path
import argparse
import json
import re
import shutil
from urllib.parse import quote, unquote, urlsplit


LINK = re.compile(r"!?\[[^\]\n]*\]\(\s*(<[^>]+>|[^\s)]+)(?:\s+[\"'][^\n]*[\"'])?\s*\)")
REFERENCE = re.compile(r"^\s*\[[^\]]+\]:\s*(<[^>]+>|\S+)")
HREF = re.compile(r"\bhref=[\"']([^\"']+)[\"']", re.IGNORECASE)
FENCE = re.compile(r"^ {0,3}(`{3,}|~{3,})")
SOURCE_URL = "https://github.com/BostonTechnologies/RatelDesk"
OFFLINE_ASSETS = {".png", ".jpg", ".jpeg", ".gif", ".svg", ".svgz", ".webp", ".ico",
                  ".pdf", ".css", ".woff", ".woff2", ".ttf", ".otf"}


def export_source_links(document: Path, repository: Path, destination: Path, revision: str) -> int:
    """Pin source-only references; missing offline assets remain validation errors."""
    document, repository, destination = document.resolve(), repository.resolve(), destination.resolve()
    original = document.read_bytes().decode("utf-8")
    source_document = repository / document.relative_to(destination)

    def target(raw: str) -> str:
        angle = raw.startswith("<") and raw.endswith(">")
        url = urlsplit(raw[1:-1] if angle else raw)
        if url.scheme or url.netloc or url.path.startswith("/"):
            return raw
        local = unquote(url.path)
        if (document.parent / local).exists():
            return raw
        source = (source_document.parent / local).resolve()
        try:
            relative = source.relative_to(repository).as_posix()
        except ValueError:
            return raw
        if not source.exists() or source.suffix.lower() in OFFLINE_ASSETS:
            return raw
        route = "tree" if source.is_dir() else "blob"
        pinned = f"{SOURCE_URL}/{route}/{revision}/{quote(relative, safe='/')}"
        if url.query:
            pinned += "?" + url.query
        if url.fragment:
            pinned += "#" + url.fragment
        return "<" + pinned + ">" if angle else pinned

    output, fence, count = [], None, 0
    for line in original.splitlines(keepends=True):
        marker = FENCE.match(line)
        if marker:
            token = marker.group(1)
            if fence is None:
                fence = (token[0], len(token))
            elif token[0] == fence[0] and len(token) >= fence[1] and not line[marker.end():].strip():
                fence = None
            output.append(line)
            continue
        changes = {}
        if fence is None:
            for pattern in (LINK, REFERENCE, HREF):
                for match in pattern.finditer(line):
                    if pattern is LINK and match.group().startswith("!"):
                        continue
                    replacement = target(match.group(1))
                    if replacement != match.group(1):
                        changes[match.span(1)] = replacement
            for (start, end), replacement in sorted(changes.items(), reverse=True):
                line = line[:start] + replacement + line[end:]
            count += len(changes)
        output.append(line)
    if count:
        document.write_bytes("".join(output).encode("utf-8"))
    return count


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


def package_operator_guides(repository: Path, destination: Path, revision: str) -> None:
    profile = json.loads((repository / "tools/release/documentation-profile.json").read_text(encoding="utf-8"))
    for name in profile["files"]:
        relative = Path(name)
        if relative.parts[0] != "docs" or ".." in relative.parts:
            raise ValueError(f"invalid operator guide path: {name}")
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(repository / relative, target)
    index = "# RatelDesk operator guides\n\n"
    for label, path in profile["sections"].items():
        index += f"- [{label}]({path})\n"
    index += (f"\n[Release engineering and history]({SOURCE_URL}/tree/{revision}/docs/release-notes) · "
              f"[Source documentation and implementation evidence]({SOURCE_URL}/tree/{revision}/docs)\n")
    (destination / "docs/README.md").write_text(index, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("destination", type=Path)
    parser.add_argument("--flat-deployment", action="store_true")
    parser.add_argument("--source-revision", required=True, help="Pin exported source-only links to this full Git commit SHA.")
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-f]{40}", args.source_revision):
        parser.error("--source-revision must be a full lowercase Git commit SHA")
    repository = Path(__file__).resolve().parents[2]
    destination = args.destination
    destination.mkdir(parents=True, exist_ok=True)
    package_operator_guides(repository, destination, args.source_revision)
    shutil.copytree(repository / "licenses", destination / "licenses", dirs_exist_ok=True)
    for name in ("SECURITY.md", ".env.example"):
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
        package_client_helpers(repository, destination)
    for document in (destination / "docs").rglob("*.md"):
        export_source_links(document, repository, destination, args.source_revision)
    export_source_links(destination / "SECURITY.md", repository, destination, args.source_revision)


if __name__ == "__main__":
    main()
