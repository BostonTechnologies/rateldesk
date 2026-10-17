#!/usr/bin/env python3
"""Prepare lossless environment-only source candidates; never edit deployment sources.

Compose owns interpolation, merge semantics and validation. Vendored PyYAML only
locates scalar spans in the owner's original text. Every candidate is rendered
through the exact saved owner context and must have precisely the requested
configuration impact before a review can be returned.
"""
import argparse
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import sys

MAX_BYTES = 1024 * 1024
MAX_FILES = 32
_spec = importlib.util.spec_from_file_location("_rateldesk_yaml", Path(__file__).with_name("vendor") / "yaml" / "__init__.py",
                                             submodule_search_locations=[str(Path(__file__).with_name("vendor") / "yaml")])
yaml = importlib.util.module_from_spec(_spec)
sys.modules[_spec.name] = yaml
_spec.loader.exec_module(yaml)


class SourceReviewError(Exception):
    """Bounded operator action without raw source, secret values or process errors."""


def fail(code, action):
    raise SourceReviewError(f"{code}: {action}")


def digest(content):
    return hashlib.sha256(content).hexdigest()


def read_file(path, private=False):
    path = Path(path)
    try:
        info = path.lstat()
        if not path.is_absolute() or not stat.S_ISREG(info.st_mode):
            raise OSError()
        if private and (info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o077):
            raise OSError()
        with path.open("rb") as stream:
            content = stream.read(MAX_BYTES + 1)
        if len(content) > MAX_BYTES:
            raise OSError()
        return content
    except OSError:
        fail("source_review_unreadable", "Restore the selected regular source/checkpoint and its private checkpoint permissions, then review again.")


def mapping(node):
    if not isinstance(node, yaml.MappingNode):
        fail("source_review_shape", "Automatic source review supports block service/environment mappings and literal environment lists. Edit this owning source explicitly, then rerun review.")
    result = {}
    for key, value in node.value:
        if not isinstance(key, yaml.ScalarNode) or key.value in result or key.value == "<<":
            fail("source_review_ambiguous", "The edited source has duplicate, merged or nonliteral mapping keys. Make this service's owning environment explicit before review.")
        result[key.value] = (key, value)
    return result


def parse_source(content):
    try:
        text = content.decode("utf-8")
        is_json = False
        if text.lstrip("\ufeff \r\n\t").startswith("{"):
            try:
                json.loads(text.removeprefix("\ufeff"))
                is_json = True
            except ValueError:
                pass  # YAML flow mappings remain an explicit unsupported edit.
        document = yaml.compose(text, Loader=yaml.SafeLoader)
        root = mapping(document)
        if "include" in root:
            fail("source_review_dependency", "Automatic editing cannot bind Compose include/extends sources. Review and edit the original owner-managed source explicitly.")
        services = mapping(root["services"][1]) if "services" in root else {}
        return {"text": text, "services": services, "tokens": list(yaml.scan(text, Loader=yaml.SafeLoader)), "is_json": is_json}
    except (UnicodeError, yaml.YAMLError):
        fail("source_review_yaml", "The saved source is not a supported UTF-8 YAML document. Validate its original Compose source before review; no source was rewritten.")


def environment_entries(document, service, changed_keys):
    _, node = document["services"][service]
    fields = mapping(node)
    if "extends" in fields:
        fail("source_review_dependency", "Edit the owning extended service explicitly; automatic source review cannot rebase an inherited service.")
    if node.flow_style and not document["is_json"]:
        fail("source_review_flow", "Expand this service's flow mapping in its owning source before automatic review; setup will not reformat it.")
    # Preserve aliases in unrelated fields (labels, healthchecks, etc.). Only
    # shared service/environment nodes can make this scalar edit affect another
    # owner; Compose additionally checks the entire candidate's exact impact.
    service_key = document["services"][service][0]
    first_field = node.value[0][0].start_mark.index if node.value else node.end_mark.index
    if node.start_mark.index < service_key.end_mark.index or any(
            isinstance(token, yaml.AnchorToken) and service_key.end_mark.index <= token.start_mark.index < first_field
            for token in document["tokens"]):
        fail("source_review_alias", "Make the edited service/environment explicit before review; setup will not change a shared YAML anchor or alias.")
    if "environment" not in fields:
        return fields, None, {}
    env_key, env = fields["environment"]
    if env.start_mark.index < env_key.end_mark.index or any(
            isinstance(token, (yaml.AliasToken, yaml.AnchorToken)) and env_key.end_mark.index <= token.start_mark.index < env.end_mark.index
            for token in document["tokens"]):
        fail("source_review_alias", "Make the edited environment explicit before review; setup will not change a shared YAML anchor or alias.")
    entries = {}
    if isinstance(env, yaml.MappingNode):
        if env.flow_style and not document["is_json"]:
            fail("source_review_flow", "Expand this environment flow mapping in its owning source before automatic review; setup will not reformat it.")
        for key, (_, value) in mapping(env).items():
            entries[key] = value
    elif isinstance(env, yaml.SequenceNode) and (not env.flow_style or document["is_json"]):
        for item in env.value:
            if not isinstance(item, yaml.ScalarNode) or item.style in ("|", ">"):
                fail("source_review_shape", "Use a literal block environment list before automatic source review.")
            key = item.value.partition("=")[0]
            if key in entries:
                fail("source_review_ambiguous", "Remove duplicate environment keys in the edited source before review.")
            entries[key] = item
    else:
        fail("source_review_shape", "Use an explicit block environment mapping/list before automatic source review.")
    # Docker environment names are case-sensitive. Only aliases of a setting
    # being edited can make its .NET configuration target ambiguous; preserve
    # unrelated pairs such as HTTP_PROXY/http_proxy as separate source entries.
    targets = {key.replace(":", "__").casefold() for key in changed_keys}
    canonical = [key.replace(":", "__").casefold() for key in entries
                 if key.replace(":", "__").casefold() in targets]
    if len(canonical) != len(set(canonical)):
        fail("source_review_ambiguous", "Remove canonical duplicates of the requested environment setting in its owning source before review.")
    return fields, env, entries


def literal(value):
    # Compose interpolates even quoted YAML scalars. $$ is its supported literal
    # dollar escape; semantic validation below confirms the exact final value.
    return json.dumps(value.replace("$", "$$"), ensure_ascii=False)


def scalar_replacement(value, node, document):
    if document["is_json"] or node.style == '"':
        return literal(value)
    escaped = value.replace("$", "$$")
    if node.style == "'":
        return "'" + escaped.replace("'", "''") + "'"
    if node.style is None and not any(character in escaped for character in "\r\n\0"):
        try:
            parsed = yaml.compose(escaped, Loader=yaml.SafeLoader)
            if isinstance(parsed, yaml.ScalarNode) and parsed.style is None and parsed.value == escaped:
                return escaped
        except yaml.YAMLError:
            pass
    return literal(value)


def insert_position(text, node):
    end = node.end_mark.index
    line = text.rfind("\n", 0, end) + 1
    if not text[line:end].strip():
        return line
    return end


def patch_source(document, changes):
    if document["is_json"]:
        return patch_json_source(document, changes)
    text = document["text"]
    edits = []
    newline = "\r\n" if "\r\n" in text else "\n"
    for service, values in changes.items():
        fields, env, entries = environment_entries(document, service, values)
        missing = []
        for key, value in values.items():
            if key in entries:
                node = entries[key]
                if not isinstance(node, yaml.ScalarNode) or node.style in ("|", ">"):
                    fail("source_review_scalar", "Replace this multiline environment scalar explicitly in its owning source, then review again.")
                rendered = scalar_replacement(key + "=" + value if isinstance(env, yaml.SequenceNode) else value, node, document)
                edits.append((node.start_mark.index, node.end_mark.index, rendered))
            else:
                if any(existing.replace(":", "__").casefold() == key.replace(":", "__").casefold() for existing in entries):
                    fail("source_review_ambiguous", "Align the requested environment key with its existing canonical spelling before review.")
                missing.append((key, value))
        if missing:
            if env is None:
                service_node = document["services"][service][1]
                position = insert_position(text, service_node)
                indent = document["services"][service][0].start_mark.column + 2
                content = " " * indent + "environment:" + newline
                content += "".join(" " * (indent + 2) + key + ": " + literal(value) + newline for key, value in missing)
            else:
                position = insert_position(text, env)
                indent = entries[next(iter(entries))].start_mark.column if entries else fields["environment"][0].start_mark.column + 2
                if isinstance(env, yaml.SequenceNode):
                    # Scalar list marks begin after '- '.
                    indent -= 2
                    content = "".join(" " * indent + "- " + literal(key + "=" + value) + newline for key, value in missing)
                else:
                    # Mapping value marks begin after the key; use its key mark.
                    indent = env.value[0][0].start_mark.column if env.value else indent
                    content = "".join(" " * indent + key + ": " + literal(value) + newline for key, value in missing)
            if position and text[position - 1] != "\n":
                content = newline + content
            edits.append((position, position, content))
    for start, end, content in sorted(edits, reverse=True):
        text = text[:start] + content + text[end:]
    return text.encode("utf-8")


def json_insertion(text, container, members, newline):
    """Insert only new JSON members; source spans and existing bytes stay intact."""
    multiline = "\n" in text[container.start_mark.index:container.end_mark.index]
    if container.value:
        last = container.value[-1]
        last_node = last[1] if isinstance(container, yaml.MappingNode) else last
        position = last_node.end_mark.index
        indent = last[0].start_mark.column if isinstance(container, yaml.MappingNode) else last.start_mark.column
        separator = "," + (newline + " " * indent if multiline else " ")
        return position, separator + separator.join(members)
    position = container.start_mark.index + 1
    if multiline:
        indent = max(0, container.end_mark.column - 1) + 2
        separator = "," + newline + " " * indent
        return position, newline + " " * indent + separator.join(members)
    return position, ", ".join(members)


def patch_json_source(document, changes):
    text = document["text"]
    newline = "\r\n" if "\r\n" in text else "\n"
    edits = []
    for service, values in changes.items():
        fields, env, entries = environment_entries(document, service, values)
        missing = []
        for key, value in values.items():
            if key in entries:
                node = entries[key]
                if not isinstance(node, yaml.ScalarNode):
                    fail("source_review_scalar", "Replace this non-scalar environment value explicitly in its owning JSON source, then review again.")
                rendered = literal(key + "=" + value) if isinstance(env, yaml.SequenceNode) else literal(value)
                edits.append((node.start_mark.index, node.end_mark.index, rendered))
            else:
                if any(existing.replace(":", "__").casefold() == key.replace(":", "__").casefold() for existing in entries):
                    fail("source_review_ambiguous", "Align the requested environment key with its existing canonical spelling before review.")
                missing.append((key, value))
        if missing:
            if env is None:
                members = ", ".join(json.dumps(key, ensure_ascii=False) + ": " + literal(value) for key, value in missing)
                position, content = json_insertion(text, document["services"][service][1], ['"environment": {' + members + '}'], newline)
            else:
                members = [literal(key + "=" + value) if isinstance(env, yaml.SequenceNode) else json.dumps(key, ensure_ascii=False) + ": " + literal(value) for key, value in missing]
                position, content = json_insertion(text, env, members, newline)
            edits.append((position, position, content))
    for start, end, content in sorted(edits, reverse=True):
        text = text[:start] + content + text[end:]
    try:
        json.loads(text.removeprefix("\ufeff"))
    except ValueError:
        fail("source_review_invalid", "The bounded JSON source edit could not preserve valid JSON. Edit the owning environment explicitly and review again.")
    return text.encode("utf-8")


def safe_source_diff(source, document, changes):
    # Show only the actual edited keys/shape. Neighboring YAML can contain
    # identities and credentials; their context and values never enter review.
    name = json.dumps(Path(source).name, ensure_ascii=True)
    lines = ["--- " + name + " (original)", "+++ " + name + " (candidate)"]
    for service, values in changes.items():
        fields, env, entries = environment_entries(document, service, values)
        lines.append("@@ services." + service + ".environment @@")
        if env is None and not document["is_json"]:
            indent = document["services"][service][0].start_mark.column + 2
            lines.append("+" + " " * indent + "environment:")
        for key in values:
            if document["is_json"]:
                prefix = json.dumps(key + "=") if isinstance(env, yaml.SequenceNode) else json.dumps(key) + ": "
                if key in entries:
                    lines.append("- " + prefix + "[previous value redacted]")
                lines.append("+ " + prefix + "[reviewed value]")
                continue
            if isinstance(env, yaml.SequenceNode):
                indent = env.value[0].start_mark.column - 2
                prefix = " " * indent + "- " + key + "="
            else:
                indent = env.value[0][0].start_mark.column if env is not None and env.value else fields.get("environment", document["services"][service])[0].start_mark.column + 2
                if env is None:
                    indent += 2
                prefix = " " * indent + key + ": "
            if key in entries:
                lines.append("-" + prefix + "[previous value redacted]")
            lines.append("+" + prefix + "[reviewed value]")
    return "\n".join(lines)


def validate_overlay(overlay):
    if not isinstance(overlay, dict) or set(overlay) != {"services"} or not isinstance(overlay["services"], dict) or not overlay["services"]:
        fail("source_review_overlay", "Review requires a nonempty environment-only service change; topology alternatives need an explicit separate owner review.")
    for service, fields in overlay["services"].items():
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", service) or not isinstance(fields, dict) or set(fields) != {"environment"}:
            fail("source_review_overlay", "Only existing-service environment changes are supported by this source-preserving review.")
        environment = fields["environment"]
        if not isinstance(environment, dict) or not environment or len(environment) > 256:
            fail("source_review_overlay", "Review requires bounded explicit environment changes.")
        for key, value in environment.items():
            if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,255}", key) or not isinstance(value, str) or len(value) > 65536 or "\0" in value:
                fail("source_review_overlay", "Use bounded literal environment keys and string values for source-preserving review.")


def default_renderer(context):
    spec = importlib.util.spec_from_file_location("rateldesk_source_launcher", Path(__file__).with_name("setup-http-mcp.py"))
    launcher = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(launcher)
    docker = launcher.Docker()
    docker.selection = dict(context["dockerSelection"])
    docker.daemon_identity = context["daemonIdentity"]
    if docker.selection.get(launcher.compose_toolchain.BINARY_KEY):
        docker.compose_tool = launcher.compose_toolchain.Selection(docker.selection[launcher.compose_toolchain.BINARY_KEY],
            docker.selection.get(launcher.compose_toolchain.DIGEST_KEY), launcher.fail)
    def render(sources):
        command = list(context["compose"])
        replacements = dict(zip(context["sources"], sources))
        for index in range(1, len(command)):
            if command[index - 1] in ("-f", "--file"):
                if command[index] not in replacements:
                    fail("source_review_context", "The saved ordered Compose inputs changed. Restart host source review.")
                command[index] = replacements[command[index]]
        try:
            docker.bind_daemon()
            result = json.loads(docker.run(command + ["config", "--format", "json"], owner_tool=True))
            docker.bind_daemon()
            return launcher.compose_runtime_model(result)
        except (launcher.Prerequisite, ValueError, UnicodeError):
            fail("source_review_compose", "The deployment owner's exact Compose tool rejected this source/candidate or its saved Docker selection changed. Correct the owning source/tool before review; no deployment source was written.")
    return render


def prepare_review(context, overlay, renderer=None):
    validate_overlay(overlay)
    if context.get("schemaVersion") != 1 or context.get("manager") not in ("external-compose", "release-compose") or context.get("ownerComposeRequired") is not True:
        fail("source_review_context", "Restore the private source-review context and explicit deployment owner, then review again.")
    sources = context.get("sources")
    if not isinstance(sources, list) or not 1 <= len(sources) <= MAX_FILES or len(set(sources)) != len(sources):
        fail("source_review_context", "Restore the original ordered Compose source list before review.")
    output = Path(context["outputDirectory"])
    info = output.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o077:
        fail("source_review_context", "Use the existing private setup output directory for source candidates.")
    snapshot_bytes = read_file(context["effectiveSnapshot"], private=True)
    try:
        snapshot = json.loads(snapshot_bytes)
    except (ValueError, UnicodeError):
        fail("source_review_context", "Restore the effective source checkpoint before review.")
    originals = {source: read_file(source) for source in sources}
    dependencies = {}
    absent_dependencies = []
    for record in snapshot.get("x-rateldesk-source-files", []):
        if record.get("present") is False:
            if Path(record["path"]).exists():
                fail("source_review_changed", "An optional source appeared after discovery. Restart host source review.")
            absent_dependencies.append(record["path"])
            continue
        content = read_file(record["path"])
        if digest(content).casefold() != record.get("sha256", "").casefold():
            fail("source_review_changed", "A source/dependency changed after discovery. Restart host source review.")
        dependencies[record["path"]] = digest(content)
    documents = {}
    for source, content in originals.items():
        try:
            documents[source] = parse_source(content)
        except SourceReviewError as error:
            error.source = source
            raise
    planned = {}
    for service, fields in overlay["services"].items():
        owners = [source for source in sources if service in documents[source]["services"]]
        if not owners:
            fail("source_review_service", "A requested service is absent from the owner's saved sources. Topology changes require a separate explicit review.")
        source_entries = {}
        for source in owners:
            try:
                source_entries[source] = environment_entries(documents[source], service, fields["environment"])[2]
            except SourceReviewError as error:
                error.source = source
                raise
        for key, value in fields["environment"].items():
            spellings = {existing for entries in source_entries.values() for existing in entries
                         if existing.replace(":", "__").casefold() == key.replace(":", "__").casefold()}
            if len(spellings) > 1:
                fail("source_review_ambiguous", "Remove canonical duplicates of the requested environment setting across its owning sources before review.")
            if spellings and key not in spellings:
                fail("source_review_ambiguous", "Align the requested environment key with its existing canonical spelling before review.")
            owner = next((source for source in reversed(owners) if key in source_entries[source]), owners[-1])
            planned.setdefault(owner, {}).setdefault(service, {})[key] = value
    render = renderer or default_renderer(context)
    original_model = render(sources)
    expected = copy.deepcopy(original_model)
    for service, fields in overlay["services"].items():
        if service not in expected.get("services", {}):
            fail("source_review_service", "The actual owner-rendered deployment has no requested service. Restart source review.")
        expected["services"][service].setdefault("environment", {}).update(fields["environment"])
    # Both the source renderer and C# model must start from the exact same
    # complete Compose model, including unrelated services and settings.
    snapshot_model = {key: value for key, value in snapshot.items() if not key.startswith("x-rateldesk-")}
    if original_model != snapshot_model:
        fail("source_review_changed", "The owner's current rendered deployment differs from discovery. Restart review with that exact owner tool and source context.")
    revision = digest(json.dumps(overlay, sort_keys=True, separators=(",", ":")).encode())
    candidates = []
    rendered_sources = list(sources)
    for index, source in enumerate(sources):
        if source not in planned:
            continue
        try:
            candidate_bytes = patch_source(documents[source], planned[source])
        except SourceReviewError as error:
            error.source = source
            raise
        candidate_path = output / ("source-review-" + digest(originals[source] + revision.encode())[:24] + "-" + str(index) + Path(source).suffix)
        if candidate_path.exists() and read_file(candidate_path, private=True) != candidate_bytes:
            fail("source_review_candidate_changed", "An existing deterministic source candidate was changed. Start a new review in a clean private setup directory.")
        if not candidate_path.exists():
            descriptor = os.open(candidate_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, "wb") as stream:
                stream.write(candidate_bytes)
                stream.flush()
                os.fsync(stream.fileno())
        rendered_sources[index] = str(candidate_path)
        candidates.append({"source": source, "candidate": str(candidate_path), "originalSha256": digest(originals[source]),
                           "candidateSha256": digest(candidate_bytes),
                           "safeDiff": safe_source_diff(source, documents[source], planned[source])})
    candidate_model = render(rendered_sources)
    if candidate_model != expected:
        fail("source_review_impact", "The source candidate changes configuration beyond the reviewed environment keys. Edit the owning source explicitly, then review again; no deployment source was written.")
    if read_file(context["effectiveSnapshot"], private=True) != snapshot_bytes or any(read_file(source) != before for source, before in originals.items()) or any(digest(read_file(path)) != before for path, before in dependencies.items()) or any(Path(path).exists() for path in absent_dependencies):
        fail("source_review_changed", "A source/dependency changed while its candidate was validated. Restart review before approval.")
    complete = copy.deepcopy(candidate_model)
    complete.update({key: value for key, value in snapshot.items() if key.startswith("x-rateldesk-")})
    changes = ["Update " + service + " environment setting " + key + " in its original owning source."
               for service, fields in overlay["services"].items() for key in fields["environment"]]
    return {"completeDeployment": complete, "sourceCandidates": candidates, "changes": changes}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--context", required=True)
    args = parser.parse_args()
    try:
        context = json.loads(read_file(args.context, private=True))
        content = sys.stdin.buffer.read(MAX_BYTES + 1)
        if len(content) > MAX_BYTES:
            fail("source_review_overlay", "The proposed source change exceeds the bounded review limit.")
        result = prepare_review(context, json.loads(content))
        sys.stdout.write(json.dumps(result, separators=(",", ":")) + "\n")
        return 0
    except SourceReviewError as error:
        code, _, message = str(error).partition(": ")
        sys.stdout.write(json.dumps({"error": {"code": code, "message": message, "source": getattr(error, "source", "")}}, separators=(",", ":")) + "\n")
        print(str(error), file=sys.stderr)
    except (OSError, ValueError, TypeError, KeyError):
        message = "Restore the bounded private source context and supported original YAML, then review again; no deployment source was written."
        sys.stdout.write(json.dumps({"error": {"code": "source_review_invalid", "message": message, "source": ""}}, separators=(",", ":")) + "\n")
        print("source_review_invalid: " + message, file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
