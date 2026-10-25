"""Download an archive, verify its SHA-256, and extract only the named members (standard library only)."""

import argparse
import hashlib
import os
import posixpath
import shutil
import sys
import tarfile
import tempfile
import urllib.request


def fail(archive: str, message: str) -> None:
    print(f"fetch_verified: {archive}: {message}", file=sys.stderr)
    sys.exit(1)


def download(url: str, target: str) -> str:
    digest = hashlib.sha256()
    with urllib.request.urlopen(url, timeout=120) as response, open(target, "wb") as out:
        while chunk := response.read(1024 * 1024):
            digest.update(chunk)
            out.write(chunk)
    return digest.hexdigest()


def is_safe_member(name: str) -> bool:
    normalized = posixpath.normpath(name)
    return not (
        posixpath.isabs(name)
        or normalized in ("", ".", "..")
        or normalized.startswith("../")
        or "\\" in name
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", required=True)
    parser.add_argument("--sha256", required=True)
    parser.add_argument("--dest", required=True)
    parser.add_argument(
        "--member",
        action="append",
        required=True,
        help="archive member path; extracted as a file named after its last path segment",
    )
    args = parser.parse_args()

    archive = args.url.rsplit("/", 1)[-1] or args.url
    expected = args.sha256.strip().lower()

    for name in args.member:
        if not is_safe_member(name):
            fail(archive, f"unsafe member name '{name}'")

    os.makedirs(args.dest, exist_ok=True)
    with tempfile.TemporaryDirectory() as scratch:
        path = os.path.join(scratch, "archive")
        try:
            actual = download(args.url, path)
        except OSError as error:
            fail(archive, f"download failed from {args.url}: {error}")

        if actual != expected:
            fail(archive, f"SHA-256 mismatch: expected {expected}, got {actual}")

        try:
            with tarfile.open(path, mode="r:*") as tar:
                for name in args.member:
                    try:
                        info = tar.getmember(name)
                    except KeyError:
                        fail(archive, f"member '{name}' not found")
                    if not info.isreg():
                        fail(archive, f"member '{name}' is not a regular file")
                    source = tar.extractfile(info)
                    target = os.path.join(args.dest, posixpath.basename(name))
                    with source, open(target, "wb") as out:
                        shutil.copyfileobj(source, out)
        except (tarfile.TarError, OSError) as error:
            fail(archive, f"extraction failed: {error}")


if __name__ == "__main__":
    main()
