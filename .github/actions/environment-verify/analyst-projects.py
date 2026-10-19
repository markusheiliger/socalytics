#!/usr/bin/env python3
"""Discovers and validates the independent Python projects of the Analyst source area.

Recognized manifests (see the environment interface contract of the analyst-media-environment spec):

    src/analysts/sdk/pyproject.toml
    src/analysts/capabilities/<name>/pyproject.toml

With `--area agents` the helper recognizes src/agents/<name>/pyproject.toml instead (see the environment
interface contract of the agents-python-environment spec); `--area analysts` is the default.

Usage: analyst-projects.py discover [--area analysts|agents] [--root DIR] [--python-version X.Y[.Z]]
       analyst-projects.py select --mode task|phase|finalize [--changed-files FILE] [--root DIR] [--python-version X.Y[.Z]]

Prints the validated projects as JSON in dependency order (dependencies first). `select` prints only the projects
the mode and the changed paths call for, each once with its reason, plus the roots of every existing recognized
project as the covered source scopes. Contracts stay covered by the platform scope and unowned or deleted project
sources stay uncovered. A Python manifest elsewhere under src/analysts (outside Manager and ignored folders) fails as
an unsupported layout instead of disappearing from verification. Every violation is reported on
stderr as one line `project <path>: <stage>: <problem>. Recommended action: <action>` and ends with exit code 1.
"""

import argparse
import json
import os
import re
import sys
import tomllib

SDK_ROOT = "src/analysts/sdk"
CAPABILITIES_ROOT = "src/analysts/capabilities"
MANAGER_ROOT = "src/analysts/manager"
IGNORED_DIRECTORIES = {
    ".venv", "venv", "node_modules", "__pycache__", ".git", "build", "dist", "site-packages",
}
AGENTS_ROOT = "src/agents"
FIX_MANIFEST = "correct the named manifest to the documented Python product convention, or author the required environment extension, before retrying"
FIX_LAYOUT = "move the project to src/analysts/sdk or src/analysts/capabilities/<name>, or author the required environment extension, before retrying"
FIX_AGENT_MANIFEST = "correct the named manifest to the agent project convention, or author the required environment extension, before retrying"
FIX_AGENT_LAYOUT = "move the project to src/agents/<name>, or author the required environment extension, before retrying"
FIX_AGENT_ANALYST = "remove the dependency on the Analyst project or move the shared code into an agent project under src/agents/<name>, before retrying"
FIX_LOCK = "commit the consuming project's own uv.lock, generated from its committed declarations, before retrying"
FIX_RUNTIME = "correct requires-python of the named project so it admits the installed interpreter, or author the required environment extension, before retrying"


class ProjectError(Exception):
    def __init__(self, project, stage, problem, action=FIX_MANIFEST):
        super().__init__(f"project {project}: {stage}: {problem}. Recommended action: {action}")


def normalize_name(name):
    return re.sub(r"[-_.]+", "-", name).lower()


def parse_version(text):
    match = re.fullmatch(r"\s*(\d+(?:\.\d+)*)\s*", text)
    if not match:
        raise ValueError(text)
    return tuple(int(part) for part in match.group(1).split("."))


def compare(left, right):
    size = max(len(left), len(right))
    left = left + (0,) * (size - len(left))
    right = right + (0,) * (size - len(right))
    return (left > right) - (left < right)


def clause_allows(clause, version):
    match = re.fullmatch(r"\s*(~=|==|!=|<=|>=|<|>)\s*([0-9][0-9.]*(?:\.\*)?)\s*", clause)
    if not match:
        raise ValueError(clause)
    operator, operand = match.groups()
    if operand.endswith(".*"):
        if operator not in ("==", "!="):
            raise ValueError(clause)
        prefix = parse_version(operand[:-2])
        matches = version[: len(prefix)] == prefix
        return matches if operator == "==" else not matches
    target = parse_version(operand)
    if operator == "~=":
        if len(target) < 2:
            raise ValueError(clause)
        return compare(version, target) >= 0 and version[: len(target) - 1] == target[:-1]
    result = compare(version, target)
    return {
        "==": result == 0, "!=": result != 0, "<=": result <= 0, ">=": result >= 0, "<": result < 0, ">": result > 0,
    }[operator]


def specifier_allows(specifier, version):
    clauses = [clause for clause in specifier.split(",") if clause.strip()]
    if not clauses:
        raise ValueError(specifier)
    return all(clause_allows(clause, version) for clause in clauses)


def immediate_projects(root, parent):
    """Returns the immediate, searched child folders of parent that hold a pyproject.toml."""
    found = []
    folder = os.path.join(root, parent)
    if os.path.isdir(folder) and not os.path.islink(folder):
        for entry in sorted(os.listdir(folder)):
            path = os.path.join(folder, entry)
            if entry.startswith(".") or entry in IGNORED_DIRECTORIES:
                continue
            if os.path.islink(path) or not os.path.isdir(path):
                continue
            if os.path.isfile(os.path.join(path, "pyproject.toml")):
                found.append(f"{parent}/{entry}")
    return found


def candidate_manifests(root, area="analysts"):
    """Yields (relative project root) for every recognized manifest location that has a pyproject.toml."""
    if area == "agents":
        return immediate_projects(root, AGENTS_ROOT)
    found = []
    if os.path.isfile(os.path.join(root, SDK_ROOT, "pyproject.toml")):
        found.append(SDK_ROOT)
    capabilities = os.path.join(root, CAPABILITIES_ROOT)
    if os.path.isdir(capabilities) and not os.path.islink(capabilities):
        for entry in sorted(os.listdir(capabilities)):
            path = os.path.join(capabilities, entry)
            if entry.startswith(".") or entry in IGNORED_DIRECTORIES:
                continue
            if os.path.islink(path) or not os.path.isdir(path):
                continue
            if os.path.isfile(os.path.join(path, "pyproject.toml")):
                found.append(f"{CAPABILITIES_ROOT}/{entry}")
    return found


def unrecognized_manifests(root, recognized, area="analysts"):
    """Returns every pyproject.toml under the area outside the recognized project roots, Manager, and ignored folders."""
    analysts = os.path.join(root, *(AGENTS_ROOT if area == "agents" else "src/analysts").split("/"))
    found = []
    if not os.path.isdir(analysts) or os.path.islink(analysts):
        return found
    for directory, names, files in os.walk(analysts):
        relative = os.path.relpath(directory, root).replace(os.sep, "/")
        names[:] = sorted(
            name for name in names
            if not name.startswith(".") and name not in IGNORED_DIRECTORIES
            and not os.path.islink(os.path.join(directory, name))
            and f"{relative}/{name}" != MANAGER_ROOT
        )
        if "pyproject.toml" in files and relative not in recognized:
            found.append(f"{relative}/pyproject.toml")
    return found


def requirement_names(data):
    entries = list(data.get("project", {}).get("dependencies", []) or [])
    for values in (data.get("project", {}).get("optional-dependencies", {}) or {}).values():
        entries.extend(values)
    for values in (data.get("dependency-groups", {}) or {}).values():
        entries.extend(value for value in values if isinstance(value, str))
    names = set()
    for entry in entries:
        if isinstance(entry, str):
            match = re.match(r"\s*([A-Za-z0-9][A-Za-z0-9._-]*)", entry)
            if match:
                names.add(normalize_name(match.group(1)))
    return names


def load_project(root, relative, python_version, errors, area="analysts"):
    manifest = os.path.join(root, relative, "pyproject.toml")
    default_action = FIX_AGENT_MANIFEST if area == "agents" else FIX_MANIFEST

    def fail(stage, problem, action=None):
        action = action or default_action
        errors.append(ProjectError(relative, stage, problem, action))

    if os.path.islink(manifest):
        fail("project convention", "pyproject.toml is a symbolic link")
        return None
    try:
        with open(manifest, "rb") as handle:
            data = tomllib.load(handle)
    except (tomllib.TOMLDecodeError, UnicodeDecodeError) as error:
        fail("project convention", f"pyproject.toml is malformed ({error})")
        return None

    project = data.get("project")
    if not isinstance(project, dict):
        fail("project convention", "[project] table is missing")
        return None
    name = project.get("name")
    if not isinstance(name, str) or not name.strip() or "name" in (project.get("dynamic") or []):
        fail("project convention", "[project].name must be a static non-empty string")
        return None
    healthy = True
    if not isinstance(project.get("requires-python"), str):
        fail("project convention", "[project].requires-python is missing")
        healthy = False
    else:
        try:
            if not specifier_allows(project["requires-python"], python_version):
                fail("runtime compatibility",
                     f"requires-python {project['requires-python']} excludes the installed interpreter "
                     f"{'.'.join(map(str, python_version))}", FIX_RUNTIME)
                healthy = False
        except ValueError:
            fail("project convention", f"requires-python {project['requires-python']!r} is not a supported specifier")
            healthy = False
    build = data.get("build-system")
    if not isinstance(build, dict) or not build.get("requires") or not isinstance(build.get("build-backend"), str):
        fail("project convention", "[build-system] with requires and build-backend is missing")
        healthy = False
    if not os.path.isfile(os.path.join(root, relative, "uv.lock")):
        fail("locked sync", "uv.lock is missing", FIX_LOCK)
        healthy = False

    uv = (data.get("tool") or {}).get("uv") or {}
    if "workspace" in uv:
        fail("project convention", "[tool.uv.workspace] is not supported; every project is independent")
        healthy = False
    if uv.get("package") is False:
        fail("project convention", "[tool.uv].package = false disables the required package build")
        healthy = False

    required = requirement_names(data)
    dependencies = []
    sources = uv.get("sources") or {}
    if not isinstance(sources, dict):
        fail("project convention", "[tool.uv.sources] must be a table")
        sources = {}
        healthy = False
    for source_name, source in sorted(sources.items()):
        if not isinstance(source, dict):
            fail("project convention", f"source {source_name!r} must be a table")
            healthy = False
        elif source.get("workspace"):
            fail("project convention", f"source {source_name!r} uses workspace, which is not supported")
            healthy = False
        elif "path" in source:
            if not isinstance(source["path"], str):
                fail("project convention", f"source {source_name!r} has a non-string path")
                healthy = False
                continue
            if normalize_name(source_name) not in required:
                fail("project convention",
                     f"local dependency {source_name!r} is declared in [tool.uv.sources] but not in the project requirements")
                healthy = False
            dependencies.append((source_name, source["path"]))
        elif any(key in source for key in ("git", "url", "index")):
            fail("project convention", f"source {source_name!r} must be a path to a recognized project or be omitted")
            healthy = False
    if not healthy:
        return None
    return {
        "name": normalize_name(name),
        "root": relative,
        "requiresPython": project["requires-python"],
        "declaredDependencies": dependencies,
    }


def within(parent, child):
    """True when child equals parent or lies below it, compared by complete path components."""
    parent_parts = parent.split(os.sep)
    child_parts = child.split(os.sep)
    return child_parts[: len(parent_parts)] == parent_parts


def resolve_dependency(root, project, source_name, path, recognized, errors, area="analysts"):
    real_root = os.path.realpath(root)
    target = os.path.realpath(os.path.join(root, project["root"], path))
    if not within(real_root, target):
        errors.append(ProjectError(project["root"], "project convention",
                                   f"local dependency {source_name!r} points outside the repository"))
        return None
    relative = os.path.relpath(target, real_root).replace(os.sep, "/")
    if area == "agents":
        permitted = relative.startswith(AGENTS_ROOT + "/") and relative.count("/") == AGENTS_ROOT.count("/") + 1
        roots = f"{AGENTS_ROOT}/<name>"
    else:
        permitted = relative == SDK_ROOT or (
            relative.startswith(CAPABILITIES_ROOT + "/") and relative.count("/") == CAPABILITIES_ROOT.count("/") + 1
        )
        roots = f"{SDK_ROOT} and {CAPABILITIES_ROOT}/<name>"
    if not permitted:
        if area != "agents":
            action = FIX_MANIFEST
        elif relative.startswith("src/analysts/"):
            action = FIX_AGENT_ANALYST
        else:
            action = FIX_AGENT_MANIFEST
        errors.append(ProjectError(project["root"], "project convention",
                                   f"local dependency {source_name!r} at {relative} is outside the permitted ownership roots "
                                   f"({roots})", action))
        return None
    if not os.path.isfile(os.path.join(target, "pyproject.toml")):
        errors.append(ProjectError(project["root"], "project convention",
                                   f"local dependency {source_name!r} points to {relative}, which has no project"))
        return None
    if relative not in recognized:
        errors.append(ProjectError(project["root"], "project convention",
                                   f"local dependency {source_name!r} points to {relative}, which is not a valid recognized project"))
        return None
    if recognized[relative]["name"] != normalize_name(source_name):
        errors.append(ProjectError(project["root"], "project convention",
                                   f"local dependency {source_name!r} points to project {recognized[relative]['name']!r}"))
        return None
    return relative


def order_projects(projects, errors):
    """Topological order, dependencies first; reports cycles."""
    state = {}
    order = []
    reported = set()

    def visit(root, trail):
        if state.get(root) == "done":
            return
        if state.get(root) == "active":
            cycle = trail[trail.index(root):] + [root]
            key = frozenset(cycle)
            if key not in reported:
                reported.add(key)
                errors.append(ProjectError(root, "project convention",
                                           "local dependencies form a cycle: " + " -> ".join(cycle)))
            return
        state[root] = "active"
        for dependency in projects[root]["dependencies"]:
            visit(dependency, trail + [root])
        state[root] = "done"
        order.append(root)

    for root in sorted(projects):
        visit(root, [])
    return order


def analyst_project_names(root):
    """Maps the normalized static [project].name of every Analyst manifest to its root; unreadable names are skipped."""
    names = {}
    for relative in candidate_manifests(root, "analysts"):
        manifest = os.path.join(root, relative, "pyproject.toml")
        if os.path.islink(manifest):
            continue
        try:
            with open(manifest, "rb") as handle:
                data = tomllib.load(handle)
        except (tomllib.TOMLDecodeError, UnicodeDecodeError, OSError):
            continue
        project = data.get("project")
        name = project.get("name") if isinstance(project, dict) else None
        if isinstance(name, str) and name.strip() and "name" not in (project.get("dynamic") or []):
            names.setdefault(normalize_name(name), relative)
    return names


def discover(root, python_version, area="analysts"):
    errors = []
    loaded = {}
    candidates = candidate_manifests(root, area)
    recognized_at = f"{AGENTS_ROOT}/<name>" if area == "agents" else f"{SDK_ROOT} and {CAPABILITIES_ROOT}/<name>"
    for manifest in unrecognized_manifests(root, set(candidates), area):
        errors.append(ProjectError(manifest, "project convention",
                                   f"unsupported layout: Python manifests are recognized only at {recognized_at}, "
                                   f"so its source stays uncovered", FIX_AGENT_LAYOUT if area == "agents" else FIX_LAYOUT))
    for relative in candidates:
        project = load_project(root, relative, python_version, errors, area)
        if project:
            loaded[relative] = project

    identities = {}
    for relative in sorted(loaded):
        identities.setdefault(loaded[relative]["name"], []).append(relative)
    for name, roots in sorted(identities.items()):
        if len(roots) > 1:
            errors.append(ProjectError(roots[1], "project convention",
                                       f"project name {name!r} duplicates {roots[0]}"))
    duplicates = {relative for roots in identities.values() if len(roots) > 1 for relative in roots[1:]}
    if area == "agents":
        analyst_names = analyst_project_names(root)
        for relative in sorted(loaded):
            name = loaded[relative]["name"]
            if relative not in duplicates and name in analyst_names:
                errors.append(ProjectError(relative, "project convention",
                                           f"project name {name!r} duplicates Analyst project {analyst_names[name]}",
                                           FIX_AGENT_MANIFEST))
                duplicates.add(relative)

    recognized = {relative: project for relative, project in loaded.items() if relative not in duplicates}
    # Every candidate manifest is recognized, even an invalid one, so only valid targets resolve below.
    for relative, project in recognized.items():
        project["dependencies"] = []
        for source_name, path in project.pop("declaredDependencies"):
            target = resolve_dependency(root, project, source_name, path, recognized, errors, area)
            if target and target not in project["dependencies"]:
                project["dependencies"].append(target)
        project["dependencies"].sort()

    ordered = order_projects(recognized, errors)
    return [recognized[relative] for relative in ordered], errors


ENVIRONMENT_FOLDERS = (".github/actions/environment-setup", ".github/actions/environment-verify")
SHARED_CONTRACT_FOLDERS = ("contracts/analysis", "contracts/common")


def components(path):
    return [part for part in path.strip().replace("\\", "/").split("/") if part and part != "."]


def under(folder, path):
    """True when path equals folder or lies below it, compared by complete path components."""
    folder_parts = components(folder)
    return components(path)[: len(folder_parts)] == folder_parts


def shared_contract_change(path, area):
    parts = components(path)
    if area == "agents":
        if len(parts) >= 3 and parts[0] == "contracts" and parts[1].startswith("agent-"):
            return True
        return under("contracts/common", path)
    return any(under(folder, path) for folder in SHARED_CONTRACT_FOLDERS)


def select(projects, mode, changed, area="analysts"):
    """Returns the selected projects in dependency order, each once, with the reason it was selected."""
    by_root = {project["root"]: project for project in projects}
    reasons = {}
    if mode == "finalize":
        reasons = {root: "finalize" for root in by_root}
    else:
        paths = [path for path in changed if components(path)]
        if any(under(folder, path) for path in paths for folder in ENVIRONMENT_FOLDERS):
            reasons = {root: "environment change" for root in by_root}
        elif any(shared_contract_change(path, area) for path in paths):
            reasons = {root: "shared contract change" for root in by_root}
        else:
            direct = {root for root in by_root if any(under(root, path) for path in paths)}
            reasons = {root: "direct change" for root in direct}
            pending = list(direct)
            while pending:
                changed_root = pending.pop()
                for project in projects:
                    if changed_root in project["dependencies"] and project["root"] not in reasons:
                        reasons[project["root"]] = "local-dependency change"
                        pending.append(project["root"])
    return [dict(project, reason=reasons[project["root"]]) for project in projects if project["root"] in reasons]


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["discover", "select"])
    parser.add_argument("--area", choices=["analysts", "agents"], default="analysts")
    parser.add_argument("--root", default=".")
    parser.add_argument("--mode", choices=["task", "phase", "finalize"], default="task")
    parser.add_argument("--changed-files", default=None)
    parser.add_argument("--python-version", default=None)
    arguments = parser.parse_args(argv)
    try:
        version = parse_version(arguments.python_version) if arguments.python_version else sys.version_info[:3]
    except ValueError:
        print(f"--python-version {arguments.python_version!r} is not a numeric version", file=sys.stderr)
        return 2
    projects, errors = discover(arguments.root, version, arguments.area)
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 1
    if arguments.command == "select":
        changed = []
        if arguments.changed_files and os.path.isfile(arguments.changed_files):
            with open(arguments.changed_files, encoding="utf-8") as handle:
                changed = handle.read().splitlines()
        result = {"projects": select(projects, arguments.mode, changed, arguments.area), "covered": [project["root"] for project in projects]}
    else:
        result = {"projects": projects}
    json.dump(result, sys.stdout, indent=2)
    print()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
