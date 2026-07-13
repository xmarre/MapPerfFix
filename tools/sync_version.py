#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import os
import re
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
VERSION_FILE = ROOT / "version.txt"
ASSEMBLY_INFO = ROOT / "MapPerfFix" / "Properties" / "AssemblyInfo.cs"
MODULE_XML = ROOT / "MapPerfFix" / "SubModule.xml"
VERSION_PATTERN = re.compile(
    r"^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)$"
)


def read_version() -> str:
    version = VERSION_FILE.read_text(encoding="utf-8").strip()
    if VERSION_PATTERN.fullmatch(version) is None:
        raise SystemExit(
            "version.txt must contain a canonical semantic version in "
            "MAJOR.MINOR.PATCH form"
        )
    return version


def render_assembly_info(source: str, version: str) -> str:
    assembly_version = version + ".0"
    source, assembly_count = re.subn(
        r'\[assembly:\s*AssemblyVersion\("[^"]+"\)\]',
        '[assembly: AssemblyVersion("' + assembly_version + '")]',
        source,
    )
    source, file_count = re.subn(
        r'\[assembly:\s*AssemblyFileVersion\("[^"]+"\)\]',
        '[assembly: AssemblyFileVersion("' + assembly_version + '")]',
        source,
    )
    if assembly_count != 1 or file_count != 1:
        raise SystemExit(
            "AssemblyInfo.cs must contain exactly one assembly and file version attribute"
        )
    return source


def render_module_xml(source: str, version: str) -> str:
    pattern = re.compile(r'<Version\s+value="v[^"]+"\s*/>')
    matches = list(pattern.finditer(source))
    if len(matches) != 1:
        raise SystemExit("SubModule.xml must contain exactly one module Version element")

    match = matches[0]
    replacement = '<Version value="v' + version + '" />'
    return source[:match.start()] + replacement + source[match.end():]


def stage_bytes(path: Path, data: bytes) -> Path:
    descriptor, temporary_name = tempfile.mkstemp(
        prefix="." + path.name + ".",
        suffix=".tmp",
        dir=str(path.parent),
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, path.stat().st_mode & 0o777)
        return temporary
    except Exception:
        try:
            os.close(descriptor)
        except OSError:
            pass
        try:
            temporary.unlink()
        except OSError:
            pass
        raise


def write_transaction(updates: list[tuple[Path, str]]) -> None:
    originals = {path: path.read_bytes() for path, _ in updates}
    staged: dict[Path, Path] = {}
    committed: list[Path] = []

    try:
        for path, content in updates:
            staged[path] = stage_bytes(path, content.encode("utf-8"))

        for path, _ in updates:
            os.replace(staged[path], path)
            staged.pop(path)
            committed.append(path)
    except Exception as exception:
        rollback_errors = []
        for path in reversed(committed):
            rollback = None
            try:
                rollback = stage_bytes(path, originals[path])
                os.replace(rollback, path)
                rollback = None
            except Exception as rollback_exception:
                rollback_errors.append(path.name + ": " + str(rollback_exception))
            finally:
                if rollback is not None:
                    try:
                        rollback.unlink()
                    except OSError:
                        pass

        detail = "version metadata update failed and committed files were rolled back"
        if rollback_errors:
            detail += "; rollback failures: " + ", ".join(rollback_errors)
        raise SystemExit(detail + ": " + str(exception)) from exception
    finally:
        for temporary in staged.values():
            try:
                temporary.unlink()
            except OSError:
                pass


def main() -> None:
    check_only = "--check" in sys.argv[1:]
    version = read_version()

    assembly_source = ASSEMBLY_INFO.read_text(encoding="utf-8")
    module_source = MODULE_XML.read_text(encoding="utf-8")
    expected_assembly = render_assembly_info(assembly_source, version)
    expected_module = render_module_xml(module_source, version)

    updates = []
    if assembly_source != expected_assembly:
        updates.append((ASSEMBLY_INFO, expected_assembly))
    if module_source != expected_module:
        updates.append((MODULE_XML, expected_module))

    if check_only:
        if updates:
            stale = [str(path.relative_to(ROOT)) for path, _ in updates]
            raise SystemExit(
                "version-derived files are stale: " + ", ".join(stale) +
                "; run python3 tools/sync_version.py"
            )
        print("version synchronization passed: " + version)
        return

    if updates:
        write_transaction(updates)
    print("synchronized version-derived files to " + version)


if __name__ == "__main__":
    main()
