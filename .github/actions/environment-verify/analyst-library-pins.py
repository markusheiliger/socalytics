#!/usr/bin/env python3
"""Checks a project's uv.lock against the pinned Analyst computer-vision library declaration.

Usage: analyst-library-pins.py --declaration <file> --lock uv.lock --project <root>

The check only reads. It enforces the whole pinned closure (the `library` and `dependency` wheels of the
declaration) only when the lock contains at least one wheel with the role `library`; the packages matching
`deniedPackages` are rejected in every project. The first finding is printed on stderr as one line

    project <root>: library pin: <library>: <kind>: locked <value>, pinned <value>. Recommended action: <action>

and ends with exit code 1 (kinds: version, platform-tag, digest, source-distribution, missing-wheel,
disallowed-package).
"""

import argparse
import fnmatch
import json
import posixpath
import re
import sys
import tomllib
from urllib.parse import unquote, urlparse

ACTION = (
    "align the project's uv.lock with the pinned library set or remove the disallowed package and retry "
    "verification; a change of a pin is a standalone environment change"
)


class Finding(Exception):
    def __init__(self, library, kind, locked, pinned):
        super().__init__(library)
        self.library = library
        self.kind = kind
        self.locked = locked
        self.pinned = pinned


def normalize(name):
    return re.sub(r"[-_.]+", "-", str(name)).lower()


def file_name(url):
    return posixpath.basename(unquote(urlparse(str(url)).path))


def digest_of(entry):
    value = str(entry.get("hash", ""))
    return value if value.startswith("sha256:") else f"sha256:{value}"


def load(path):
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def denied_name(name, patterns):
    return any(fnmatch.fnmatchcase(name, pattern) for pattern in patterns)


def check_denied(packages, patterns):
    for package in packages:
        name = normalize(package.get("name", ""))
        if denied_name(name, patterns):
            raise Finding(name, "disallowed-package", name, "none")


def check_pin(pin, packages):
    name = normalize(pin["name"])
    entries = [package for package in packages if normalize(package.get("name", "")) == name]
    if not entries:
        raise Finding(name, "missing-wheel", "none", pin["fileName"])
    matching = [entry for entry in entries if entry.get("version") == pin["version"]]
    if not matching:
        raise Finding(name, "version", entries[0].get("version", "unknown"), pin["version"])
    wheels = [wheel for entry in matching for wheel in entry.get("wheels", [])]
    if not wheels:
        sdists = [entry["sdist"] for entry in matching if entry.get("sdist")]
        if sdists:
            raise Finding(name, "source-distribution", file_name(sdists[0].get("url", "")) or "sdist", pin["fileName"])
        raise Finding(name, "missing-wheel", "none", pin["fileName"])
    wanted = [wheel for wheel in wheels if file_name(wheel.get("url", "")) == pin["fileName"]]
    if not wanted:
        raise Finding(name, "platform-tag", file_name(wheels[0].get("url", "")), pin["fileName"])
    expected = f"sha256:{pin['sha256']}"
    if not any(digest_of(wheel) == expected for wheel in wanted):
        raise Finding(name, "digest", digest_of(wanted[0]), expected)


def check(declaration, lock):
    packages = lock.get("package", [])
    check_denied(packages, [normalize(pattern) if "*" not in pattern else pattern.lower() for pattern in declaration.get("deniedPackages", [])])
    pins = declaration.get("wheels", [])
    libraries = {normalize(pin["name"]) for pin in pins if pin.get("role") == "library"}
    if not any(normalize(package.get("name", "")) in libraries for package in packages):
        return
    for pin in pins:
        check_pin(pin, packages)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--declaration", required=True)
    parser.add_argument("--lock", required=True)
    parser.add_argument("--project", required=True)
    arguments = parser.parse_args()
    try:
        check(load_json(arguments.declaration), load(arguments.lock))
    except Finding as finding:
        print(
            f"project {arguments.project}: library pin: {finding.library}: {finding.kind}: "
            f"locked {finding.locked}, pinned {finding.pinned}. Recommended action: {ACTION}",
            file=sys.stderr,
        )
        return 1
    except (OSError, ValueError, KeyError) as error:
        print(
            f"project {arguments.project}: library pin: uv.lock: unreadable: locked {type(error).__name__}, "
            f"pinned readable. Recommended action: {ACTION}",
            file=sys.stderr,
        )
        return 1
    return 0


def load_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


if __name__ == "__main__":
    sys.exit(main())
