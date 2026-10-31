#!/usr/bin/env python3
"""Licence and SBOM check for the pinned Analyst computer-vision wheels (standard library only).

Exits 0 and writes a CycloneDX 1.6 SBOM with notices beside it, or exits 1 printing one line per finding:
`<wheel|library>: <kind>: <problem>`.
"""

import argparse
import hashlib
import json
import os
import re
import struct
import sys
import zipfile

KINDS = (
    "denied-license", "unknown-license", "not-allow-listed", "evidence-mismatch", "table-gap", "evidence-gap",
    "denied-library", "lgpl-not-distinct", "lgpl-not-dynamic", "not-in-sbom", "missing-digest",
)

# Identifiers that are recognised but not allow-listed; anything else is an unknown licence.
KNOWN_LICENSES = {
    "AFL-3.0", "AGPL-1.0-only", "AGPL-1.0-or-later", "AGPL-3.0-only", "AGPL-3.0-or-later", "Apache-1.0",
    "Apache-1.1", "Artistic-2.0", "BlueOak-1.0.0", "BSD-1-Clause", "BSD-2-Clause-Patent", "BSD-3-Clause-Clear",
    "BSD-4-Clause", "BSL-1.0", "CC-BY-3.0", "CC-BY-4.0", "CC-BY-NC-4.0", "CC-BY-SA-4.0", "CDDL-1.0", "CDDL-1.1",
    "curl", "EPL-1.0", "EPL-2.0", "EUPL-1.2", "FTL", "GPL-1.0-only", "GPL-1.0-or-later", "GPL-2.0-only",
    "GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later", "HPND", "IJG", "JasPer-2.0", "LGPL-2.0-only",
    "LGPL-2.0-or-later", "LGPL-2.1-only", "LGPL-2.1-or-later", "LGPL-3.0-only", "LGPL-3.0-or-later",
    "libpng-2.0", "MIT-0", "MIT-CMU", "MS-PL", "MS-RL", "NCSA", "OpenSSL", "Python-2.0", "SSPL-1.0", "Unlicense",
    "WTFPL", "X11", "Zlib", "zlib-acknowledgement",
}

CLASSIFIERS = {
    "MIT License": {"MIT"},
    "Apache Software License": {"Apache-2.0"},
    "BSD License": {"BSD-2-Clause", "BSD-3-Clause", "0BSD"},
    "ISC License (ISCL)": {"ISC"},
    "Python Software Foundation License": {"PSF-2.0"},
    "Mozilla Public License 2.0 (MPL 2.0)": {"MPL-2.0"},
    "zlib/libpng License": {"Zlib"},
    "GNU General Public License v3 (GPLv3)": {"GPL-3.0-only"},
    "GNU General Public License v2 (GPLv2)": {"GPL-2.0-only"},
    "GNU Lesser General Public License v2 or later (LGPLv2+)": {"LGPL-2.1-or-later"},
    "GNU Lesser General Public License v3 (LGPLv3)": {"LGPL-3.0-only"},
}

PRIORITY = ("denied-license", "lgpl-not-dynamic", "lgpl-not-distinct", "unknown-license", "not-allow-listed")


def normalize_name(name):
    return re.sub(r"[-_.]+", "-", name).lower()


def sha256_hex(data):
    return hashlib.sha256(data).hexdigest()


# --- SPDX expressions ---------------------------------------------------------------------------------------------

def parse_expression(text):
    """Returns a tree: ('leaf', text) | ('and', [..]) | ('or', [..]); raises ValueError for a malformed expression."""
    tokens = re.findall(r"\(|\)|[^\s()]+", text)
    position = 0

    def peek():
        return tokens[position] if position < len(tokens) else None

    def operator(token, word):
        return token is not None and token.upper() == word

    def take():
        nonlocal position
        token = tokens[position]
        position += 1
        return token

    def parse_or():
        parts = [parse_and()]
        while operator(peek(), "OR"):
            take()
            parts.append(parse_and())
        return parts[0] if len(parts) == 1 else ("or", parts)

    def parse_and():
        parts = [parse_term()]
        while operator(peek(), "AND"):
            take()
            parts.append(parse_term())
        return parts[0] if len(parts) == 1 else ("and", parts)

    def parse_term():
        token = peek()
        if token is None or token in (")",) or operator(token, "AND") or operator(token, "OR") or operator(token, "WITH"):
            raise ValueError("malformed licence expression")
        if token == "(":
            take()
            inner = parse_or()
            if peek() != ")":
                raise ValueError("unbalanced parenthesis")
            take()
            return inner
        identifier = take()
        if operator(peek(), "WITH"):
            take()
            if peek() is None or peek() in ("(", ")"):
                raise ValueError("missing exception")
            identifier = identifier + " WITH " + take()
        return ("leaf", identifier)

    tree = parse_or()
    if position != len(tokens):
        raise ValueError("trailing tokens")
    return tree


def leaves(tree):
    if tree[0] == "leaf":
        return [tree[1]]
    return [leaf for child in tree[1] for leaf in leaves(child)]


def leaf_set(text):
    try:
        return {leaf.lower().rstrip("+") for leaf in leaves(parse_expression(text))}
    except ValueError:
        return None


class Policy:
    def __init__(self, declaration):
        self.allowed = {item.lower() for item in declaration.get("licenseAllowList", [])}
        self.lgpl = {item.lower() for item in declaration.get("lgplLicenses", [])}
        self.exceptions = {item.lower() for item in declaration.get("runtimeExceptionLicenses", [])}
        self.families = [item.upper() for item in declaration.get("deniedLicenseFamilies", [])]
        self.denied_libraries = [item.lower() for item in declaration.get("deniedLibraries", [])]
        self.known = {item.lower() for item in KNOWN_LICENSES}

    def classify(self, leaf):
        """Returns 'allowed', 'special' (LGPL or runtime exception), 'denied', 'unlisted', or 'unknown'."""
        lowered = " ".join(leaf.split()).lower()
        if lowered in self.exceptions:
            return "special"
        base = lowered.split(" with ")[0].rstrip("+")
        if " with " not in lowered:
            if base in self.allowed:
                return "allowed"
            if base in self.lgpl:
                return "special"
        if any(base.upper().startswith(family) for family in self.families):
            return "denied"
        if base in self.known or base in self.allowed or base in self.lgpl:
            return "unlisted"
        return "unknown"

    def evaluate(self, tree, special):
        """Returns (ok, failures) where failures is a list of (kind, leaf, detail)."""
        if tree[0] == "leaf":
            leaf = tree[1]
            verdict = self.classify(leaf)
            if verdict == "allowed":
                return True, []
            if verdict == "special":
                problem = special(leaf)
                return (True, []) if problem is None else (False, [(problem[0], leaf, problem[1])])
            kind = {"denied": "denied-license", "unlisted": "not-allow-listed", "unknown": "unknown-license"}[verdict]
            return False, [(kind, leaf, {
                "denied-license": "licence is on a denied family",
                "not-allow-listed": "licence is not on the allow-list",
                "unknown-license": "licence identifier is not recognised",
            }[kind])]
        results = [self.evaluate(child, special) for child in tree[1]]
        if tree[0] == "or":
            if any(ok for ok, _ in results):
                return True, []
            return False, [failure for _, failures in results for failure in failures]
        failures = [failure for ok, failing in results if not ok for failure in failing]
        return (not failures), failures

    def denied_library(self, name):
        lowered = name.lower()
        return next((prefix for prefix in self.denied_libraries if lowered.startswith(prefix)), None)


# --- ELF ----------------------------------------------------------------------------------------------------------

def parse_elf(data):
    """Returns (type, soname, needed) for an ELF image, or None when it is not a readable ELF."""
    if data[:4] != b"\x7fELF" or len(data) < 64 or data[4] not in (1, 2) or data[5] not in (1, 2):
        return None
    wide = data[4] == 2
    end = "<" if data[5] == 1 else ">"

    def unpack(fmt, offset):
        return struct.unpack_from(end + fmt, data, offset)[0]

    try:
        e_type = unpack("H", 16)
        if wide:
            phoff, shoff = unpack("Q", 32), unpack("Q", 40)
            phentsize, phnum, shentsize, shnum = unpack("H", 54), unpack("H", 56), unpack("H", 58), unpack("H", 60)
        else:
            phoff, shoff = unpack("I", 28), unpack("I", 32)
            phentsize, phnum, shentsize, shnum = unpack("H", 42), unpack("H", 44), unpack("H", 46), unpack("H", 48)
        word = "Q" if wide else "I"

        loads = []
        dynamic = None
        for index in range(phnum):
            at = phoff + index * phentsize
            kind = unpack("I", at)
            if wide:
                offset, vaddr, size = unpack("Q", at + 8), unpack("Q", at + 16), unpack("Q", at + 32)
            else:
                offset, vaddr, size = unpack("I", at + 4), unpack("I", at + 8), unpack("I", at + 16)
            if kind == 1:
                loads.append((vaddr, offset, size))
            elif kind == 2:
                dynamic = (offset, size)

        link = None
        for index in range(shnum if shoff else 0):
            at = shoff + index * shentsize
            if unpack("I", at + 4) != 6:
                continue
            if wide:
                offset, size, link = unpack("Q", at + 24), unpack("Q", at + 32), unpack("I", at + 40)
            else:
                offset, size, link = unpack("I", at + 16), unpack("I", at + 20), unpack("I", at + 24)
            if dynamic is None:
                dynamic = (offset, size)
            break
        if dynamic is None:
            return e_type, None, []

        width = 16 if wide else 8
        soname_offset, needed_offsets, strtab = None, [], None
        for at in range(dynamic[0], dynamic[0] + dynamic[1], width):
            tag, value = unpack(word, at), unpack(word, at + width // 2)
            if tag == 0:
                break
            if tag == 14:
                soname_offset = value
            elif tag == 1:
                needed_offsets.append(value)
            elif tag == 5:
                strtab = value

        base = None
        if strtab is not None:
            base = next((offset + strtab - vaddr for vaddr, offset, size in loads if vaddr <= strtab < vaddr + size), None)
        if base is None and link is not None and shoff:
            at = shoff + link * shentsize
            base = unpack("Q", at + 24) if wide else unpack("I", at + 16)
        if base is None:
            return e_type, None, []

        def string(offset):
            start = base + offset
            return data[start:data.index(b"\0", start)].decode("utf-8", "replace")

        soname = string(soname_offset) if soname_offset is not None else None
        return e_type, soname, [string(offset) for offset in needed_offsets]
    except (struct.error, ValueError):
        return None


# --- Wheel evidence -----------------------------------------------------------------------------------------------

class Wheel:
    def __init__(self, entry, path):
        self.entry = entry
        self.path = path
        self.archive = zipfile.ZipFile(path)
        self.members = [info.filename for info in self.archive.infolist() if not info.filename.endswith("/")]
        self._elf = None

    def read(self, member):
        return self.archive.read(member)

    def libraries(self):
        return sorted(
            member for member in self.members
            if "/" in member and member.split("/")[0].endswith(".libs") and member.count("/") == 1
            and re.search(r"\.so(\.|$)", member)
        )

    def metadata(self):
        name = next((m for m in self.members if m.count("/") == 1 and m.split("/")[0].endswith(".dist-info") and m.endswith("/METADATA")), None)
        return None if name is None else self.read(name).decode("utf-8", "replace")

    def elf_objects(self):
        if self._elf is None:
            self._elf = {}
            for member in self.members:
                with self.archive.open(member) as handle:
                    if handle.read(4) != b"\x7fELF":
                        continue
                parsed = parse_elf(self.read(member))
                if parsed is not None:
                    self._elf[member] = parsed
        return self._elf

    def close(self):
        self.archive.close()


def metadata_evidence(text):
    """Returns (license expression, free-text license, classifier identifiers) from the METADATA headers."""
    expression, free, classified = None, None, set()
    for line in text.split("\n\n", 1)[0].splitlines():
        if line.startswith("License-Expression:"):
            expression = line.split(":", 1)[1].strip()
        elif line.startswith("License:"):
            free = line.split(":", 1)[1].strip()
        elif line.startswith("Classifier: License ::"):
            classified |= CLASSIFIERS.get(line.rsplit("::", 1)[1].strip(), set())
    return expression, free, classified


def sanitize_member(member):
    return member.replace("/", "__")


# --- Check --------------------------------------------------------------------------------------------------------

def run(args):
    with open(args.declaration, encoding="utf-8") as handle:
        declaration = json.load(handle)
    policy = Policy(declaration)
    findings = []

    def finding(subject, kind, problem):
        assert kind in KINDS
        line = f"{subject}: {kind}: {problem}"
        if line not in findings:
            findings.append(line)

    rows = {}
    for row in declaration.get("licenseTable", []):
        rows[(normalize_name(row.get("wheel", "")), row.get("component", ""), row.get("kind", ""))] = row
    declared = {normalize_name(entry["name"]) for entry in declaration.get("wheels", [])}
    for row in declaration.get("licenseTable", []):
        if normalize_name(row.get("wheel", "")) not in declared:
            finding(row.get("component", "?"), "table-gap", f"licenseTable row names the wheel {row.get('wheel')}, which is not declared")

    results = []
    opened = []
    try:
        return evaluate(args, declaration, policy, findings, finding, rows, results, opened)
    finally:
        for wheel in opened:
            wheel.close()


def evaluate(args, declaration, policy, findings, finding, rows, results, opened):
    for entry in declaration.get("wheels", []):
        name = entry["name"]
        key = normalize_name(name)
        if not re.fullmatch(r"[0-9a-f]{64}", entry.get("sha256", "") or ""):
            finding(name, "missing-digest", "the declaration has no SHA-256 for the wheel")
        path = os.path.join(args.wheels, entry["fileName"])
        if not os.path.isfile(path):
            finding(name, "evidence-gap", f"wheel file {entry['fileName']} is not in the wheel directory")
            continue
        wheel = Wheel(entry, path)
        opened.append(wheel)
        results.append(check_wheel(wheel, key, rows, policy, finding))

    ffmpeg = None
    if args.ffmpeg_toolchain:
        with open(args.ffmpeg_toolchain, encoding="utf-8") as handle:
            tools = json.load(handle).get("tools", [])
        ffmpeg = next((tool for tool in tools if tool.get("id") == "ffmpeg"), None)
        if ffmpeg is None:
            finding("ffmpeg", "not-in-sbom", "the toolchain declaration has no pinned FFmpeg build")

    if findings:
        print("\n".join(findings))
        return 1

    components = [result["component"] for result in results]
    if ffmpeg is not None:
        components.append(ffmpeg_component(ffmpeg))
    bom = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.6",
        "version": 1,
        "metadata": {"component": {"type": "application", "name": "socalytics-analyst-cv-libraries"}},
        "components": components,
    }
    verify_bom(bom, results, findings, finding)
    if findings:
        print("\n".join(findings))
        return 1

    output = os.path.dirname(os.path.abspath(args.sbom))
    os.makedirs(output, exist_ok=True)
    for result in results:
        write_notices(output, result)
    with open(args.sbom, "w", encoding="utf-8") as handle:
        json.dump(bom, handle, indent=2)
        handle.write("\n")
    return 0


def check_wheel(wheel, key, rows, policy, finding):
    entry = wheel.entry
    name = entry["name"]
    row = rows.get((key, name, "wheel")) or next((r for (w, _, k), r in rows.items() if w == key and k == "wheel"), None)
    members = set(wheel.members)
    expression = None
    if row is None:
        finding(name, "table-gap", "the wheel has no licenseTable row")
    else:
        expression = row.get("licenseExpression", "")
        if " ".join(expression.split()).lower() != " ".join(entry.get("licenseExpression", "").split()).lower():
            finding(name, "evidence-mismatch", f"licenseTable says {expression}, the declaration says {entry.get('licenseExpression')}")
        if row.get("licenseText") not in members:
            finding(name, "evidence-gap", f"licence text {row.get('licenseText')} is not in the wheel")
        compare_metadata(wheel, name, expression, finding)
        check_expression(name, expression, policy, finding, lambda leaf: (
            "lgpl-not-distinct", "an LGPL or runtime-exception licence is only accepted for a bundled shared library"))

    libraries = []
    for member in wheel.libraries():
        library = member.split("/")[-1]
        denied = policy.denied_library(library)
        if denied:
            finding(library, "denied-library", f"bundled library matches the denied library prefix {denied}")
            continue
        library_row = rows.get((key, library, "library"))
        if library_row is None:
            finding(library, "not-in-sbom", f"bundled library of {name} has no licenseTable row")
            continue
        if not library_row.get("version"):
            finding(library, "table-gap", "the library row has no version")
        text = library_row.get("licenseText")
        if text not in members:
            finding(library, "evidence-gap", f"licence text {text} is not in the wheel")
        library_expression = library_row.get("licenseExpression", "")
        check_expression(library, library_expression, policy, finding,
                         lambda leaf, m=member, r=library_row: lgpl_problem(wheel, m, r))
        libraries.append((member, library, library_row))
    bundled = {member.split("/")[-1] for member in wheel.libraries()}
    for (w, component, kind) in rows:
        if w == key and kind == "library" and component not in bundled:
            finding(component, "evidence-gap", f"licenseTable row names a library that {name} does not bundle")

    return {"component": wheel_component(wheel, row, libraries), "wheel": wheel, "row": row, "libraries": libraries}


def check_expression(subject, expression, policy, finding, special):
    try:
        tree = parse_expression(expression)
    except ValueError:
        finding(subject, "unknown-license", f"licence expression {expression!r} cannot be parsed")
        return
    ok, failures = policy.evaluate(tree, special)
    if ok:
        return
    for kind in PRIORITY:
        detail = [f"{leaf}: {text}" for k, leaf, text in failures if k == kind]
        if detail:
            finding(subject, kind, "; ".join(detail))


def compare_metadata(wheel, name, expression, finding):
    text = wheel.metadata()
    if text is None:
        finding(name, "evidence-gap", "the wheel ships no METADATA")
        return
    shipped, free, classified = metadata_evidence(text)
    expected = leaf_set(expression)
    if expected is None:
        return
    if shipped:
        found = leaf_set(shipped)
        if found is not None and found != expected:
            finding(name, "evidence-mismatch", f"METADATA License-Expression is {shipped}, licenseTable says {expression}")
        return
    if free:
        found = leaf_set(free)
        if found is not None and all(leaf in {k.lower() for k in KNOWN_LICENSES} | expected for leaf in found) and found != expected:
            finding(name, "evidence-mismatch", f"METADATA License is {free}, licenseTable says {expression}")
            return
    if classified and not ({item.lower() for item in classified} & expected):
        finding(name, "evidence-mismatch", f"METADATA licence classifiers name {', '.join(sorted(classified))}, licenseTable says {expression}")


def lgpl_problem(wheel, member, row):
    if row.get("linkage") != "dynamic":
        return "lgpl-not-dynamic", "the licenseTable row does not declare linkage dynamic"
    objects = wheel.elf_objects()
    parsed = objects.get(member)
    if parsed is None or parsed[0] != 3 or not parsed[1]:
        return "lgpl-not-distinct", "the member is not an ELF shared object (ET_DYN) with a DT_SONAME"
    if not any(other != member and parsed[1] in needed for other, (_, _, needed) in objects.items()):
        return "lgpl-not-distinct", f"no other ELF in the wheel lists {parsed[1]} in DT_NEEDED"
    return None


# --- SBOM ---------------------------------------------------------------------------------------------------------

def license_entry(expression):
    return [{"expression": " ".join(expression.split())}]


def license_properties(expression):
    properties = []
    if " WITH " in expression.upper():
        licence, exception = re.split(r"\s+WITH\s+", expression, maxsplit=1, flags=re.IGNORECASE)
        properties = [
            {"name": "socalytics:license-name", "value": licence.strip()},
            {"name": "socalytics:license-exception", "value": exception.strip()},
        ]
    return properties


def wheel_component(wheel, row, libraries):
    entry = wheel.entry
    expression = row["licenseExpression"] if row else entry.get("licenseExpression", "")
    component = {
        "type": "library",
        "bom-ref": f"wheel:{entry['name']}@{entry['version']}",
        "name": entry["name"],
        "version": entry["version"],
        "purl": f"pkg:pypi/{entry['name']}@{entry['version']}",
        "supplier": {"name": entry.get("publisher", "")},
        "hashes": [{"alg": "SHA-256", "content": entry["sha256"]}],
        "licenses": license_entry(expression),
        "properties": [
            {"name": "socalytics:license-text", "value": row["licenseText"] if row else entry.get("licenseText", "")},
            {"name": "socalytics:wheel-file", "value": entry["fileName"]},
            {"name": "socalytics:role", "value": entry.get("role", "")},
        ] + license_properties(expression),
        "externalReferences": [{"type": "website", "url": entry.get("projectUrl", "")}],
    }
    nested = []
    for member, library, library_row in libraries:
        library_expression = library_row.get("licenseExpression", "")
        nested.append({
            "type": "library",
            "bom-ref": f"library:{entry['name']}:{library}",
            "name": library,
            "version": library_row.get("version", ""),
            "purl": f"pkg:generic/{library}@{library_row.get('version', '')}",
            "supplier": {"name": entry.get("publisher", "")},
            "hashes": [{"alg": "SHA-256", "content": sha256_hex(wheel.read(member))}],
            "licenses": license_entry(library_expression),
            "properties": [
                {"name": "socalytics:license-text", "value": library_row.get("licenseText", "")},
                {"name": "socalytics:wheel-member", "value": member},
            ] + license_properties(library_expression),
        })
    if nested:
        component["components"] = nested
    return component


def ffmpeg_component(tool):
    return {
        "type": "library",
        "bom-ref": f"toolchain:ffmpeg@{tool.get('version', '')}",
        "name": tool.get("name", "FFmpeg"),
        "version": tool.get("version", ""),
        "supplier": {"name": tool.get("publisher", "")},
        "hashes": [{"alg": "SHA-256", "content": tool.get("archive", {}).get("sha256", "")}],
        "licenses": license_entry("LGPL-2.1-or-later"),
        "properties": [
            {"name": "socalytics:license-text", "value": (tool.get("licenseLocations") or [""])[0]},
            {"name": "socalytics:build-variant", "value": tool.get("variant", "")},
        ],
        "externalReferences": [{"type": "distribution", "url": tool.get("archive", {}).get("url", "")}],
    }


def verify_bom(bom, results, findings, finding):
    names = {component["name"]: component for component in bom["components"]}
    for result in results:
        entry = result["wheel"].entry
        component = names.get(entry["name"])
        if component is None:
            finding(entry["name"], "not-in-sbom", "the wheel is missing from the SBOM")
            continue
        nested = {item["name"] for item in component.get("components", [])}
        for _, library, _ in result["libraries"]:
            if library not in nested:
                finding(library, "not-in-sbom", f"the bundled library is missing from the SBOM entry of {entry['name']}")


def write_notices(output, result):
    wheel = result["wheel"]
    entry = wheel.entry
    folder = os.path.join(output, entry["fileName"][:-4] if entry["fileName"].endswith(".whl") else entry["fileName"])
    os.makedirs(folder, exist_ok=True)
    texts = []
    if result["row"]:
        texts.append(result["row"]["licenseText"])
    texts.extend(row.get("licenseText") for _, _, row in result["libraries"])
    for member in dict.fromkeys(text for text in texts if text):
        data = wheel.read(member)
        target = os.path.join(folder, os.path.basename(member))
        if os.path.exists(target) and open(target, "rb").read() != data:
            target = os.path.join(folder, sanitize_member(member))
        with open(target, "wb") as handle:
            handle.write(data)
    lines = [
        f"Wheel: {entry['name']} {entry['version']}",
        f"File: {entry['fileName']}",
        f"URL: {entry.get('url', '')}",
        f"SHA-256: {entry['sha256']}",
        f"Publisher: {entry.get('publisher', '')}",
        f"Project: {entry.get('projectUrl', '')}",
        f"License: {entry.get('licenseExpression', '')}",
        f"Role: {entry.get('role', '')}",
        "Bundled libraries:",
    ]
    for member, library, row in result["libraries"]:
        lines.append(f"  {library} {row.get('version', '')} {sha256_hex(wheel.read(member))} {row.get('licenseExpression', '')}")
    if not result["libraries"]:
        lines.append("  none")
    with open(os.path.join(folder, "PROVENANCE.txt"), "w", encoding="utf-8") as handle:
        handle.write("\n".join(lines) + "\n")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--declaration", required=True)
    parser.add_argument("--wheels", required=True)
    parser.add_argument("--sbom", required=True)
    parser.add_argument("--ffmpeg-toolchain")
    return run(parser.parse_args(argv))


if __name__ == "__main__":
    sys.exit(main())
