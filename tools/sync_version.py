#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
VERSION_FILE = ROOT / "version.txt"
ASSEMBLY_INFO = ROOT / "MapPerfFix" / "Properties" / "AssemblyInfo.cs"
MODULE_XML = ROOT / "MapPerfFix" / "SubModule.xml"
VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+$")


def read_version() -> str:
    version = VERSION_FILE.read_text(encoding="utf-8").strip()
    if VERSION_PATTERN.fullmatch(version) is None:
        raise SystemExit("version.txt must contain a semantic version in MAJOR.MINOR.PATCH form")
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
        raise SystemExit("AssemblyInfo.cs must contain exactly one assembly and file version attribute")
    return source


def render_module_xml(source: str, version: str) -> str:
    source, count = re.subn(
        r'<Version\s+value="v[^"]+"\s*/>',
        '<Version value="v' + version + '" />',
        source,
        count=1,
    )
    if count != 1:
        raise SystemExit("SubModule.xml must contain exactly one module Version element")
    return source


def main() -> None:
    check_only = "--check" in sys.argv[1:]
    version = read_version()

    assembly_source = ASSEMBLY_INFO.read_text(encoding="utf-8")
    module_source = MODULE_XML.read_text(encoding="utf-8")
    expected_assembly = render_assembly_info(assembly_source, version)
    expected_module = render_module_xml(module_source, version)

    stale = []
    if assembly_source != expected_assembly:
        stale.append(str(ASSEMBLY_INFO.relative_to(ROOT)))
    if module_source != expected_module:
        stale.append(str(MODULE_XML.relative_to(ROOT)))

    if check_only:
        if stale:
            raise SystemExit(
                "version-derived files are stale: " + ", ".join(stale) +
                "; run python3 tools/sync_version.py"
            )
        print("version synchronization passed: " + version)
        return

    ASSEMBLY_INFO.write_text(expected_assembly, encoding="utf-8")
    MODULE_XML.write_text(expected_module, encoding="utf-8")
    print("synchronized version-derived files to " + version)


if __name__ == "__main__":
    main()
