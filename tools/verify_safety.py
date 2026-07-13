#!/usr/bin/env python3
from __future__ import annotations

from pathlib import Path
import hashlib
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "MapPerfFix" / "MapPerfFix.csproj"
MODULE_XML = ROOT / "MapPerfFix" / "SubModule.xml"
SUBMODULE = ROOT / "MapPerfFix" / "SubModule.cs"

EXPECTED_COMPILED = {
    "MapPerfConfig.cs",
    "MapPerfLog.cs",
    "MapPerfSettings.cs",
    "SubModule.cs",
    "Properties/AssemblyInfo.cs",
}

FORBIDDEN_SOURCE_NAMES = {
    "InitGate.cs",
    "MapIdleDrainMitigator.cs",
    "MapIdleDrainProbe.cs",
    "MapPauseSkipper.cs",
    "MsgFilter.cs",
    "PausedMapStateThrottler.cs",
    "PauseSimSkipper.cs",
    "PeriodicHubDeferrer.cs",
}

REVIEWED_METHODS = (
    "TryInstallHiddenPartyVisualPatch",
    "FindLegacyPartyVisualTick",
    "HiddenPartyVisualPrefix",
    "DisableVisualPatchIfCompatibilityChanged",
    "TryInstall",
    "ProfilePrefix",
    "ProfilePostfix",
)

EXPECTED_METHOD_HASHES = {
    "TryInstallHiddenPartyVisualPatch": "6b2c39054aa984bc5fb21bdfea3d00ea4339ee86dddd734c97a32be0a760876a",
    "FindLegacyPartyVisualTick": "0e8886ed707b4ca5521e37b25302a64ae4dbaf8a31ef178791740e4bc8db568f",
    "HiddenPartyVisualPrefix": "5f3fbc8e11395316b4db81f20250013b3c35bdbb9cf6d1d2d7fe71f06a2d0573",
    "DisableVisualPatchIfCompatibilityChanged": "96b1e8cd6cf202dbe553da154575d893bbaf1ae88998c577dd67cd5af9c14d81",
    "TryInstall": "3e96955825c1d1ff168fa6ff6caef9f2dedc0a0f256d5202a53a76e679904538",
    "ProfilePrefix": "0828ce350713c94956f872eaf7b27bf8e8303a0e04634e21fe8fab9274cf4d94",
    "ProfilePostfix": "43fa3928f7089294219005835f04f72a1b9b01303cac4e8e03ec7f2223995463",
}


def fail(message: str) -> None:
    print("ERROR: " + message, file=sys.stderr)
    raise SystemExit(1)


def normalize(path: str) -> str:
    return path.replace("\\", "/")


def strip_comments(source: str, mask_literals: bool = False) -> str:
    output: list[str] = []
    index = 0
    state = "code"
    while index < len(source):
        char = source[index]
        nxt = source[index + 1] if index + 1 < len(source) else ""
        if state == "code":
            if char == "/" and nxt == "/":
                output.extend("  ")
                index += 2
                state = "line"
                continue
            if char == "/" and nxt == "*":
                output.extend("  ")
                index += 2
                state = "block"
                continue
            if char == '"':
                output.append(" " if mask_literals else char)
                index += 1
                state = "string"
                continue
            if char == "'":
                output.append(" " if mask_literals else char)
                index += 1
                state = "char"
                continue
            output.append(char)
            index += 1
            continue
        if state == "line":
            if char == "\n":
                output.append("\n")
                state = "code"
            else:
                output.append(" ")
            index += 1
            continue
        if state == "block":
            if char == "*" and nxt == "/":
                output.extend("  ")
                index += 2
                state = "code"
            else:
                output.append("\n" if char == "\n" else " ")
                index += 1
            continue
        if state in ("string", "char"):
            if char == "\\":
                output.append(" " if mask_literals else char)
                if index + 1 < len(source):
                    output.append(" " if mask_literals else source[index + 1])
                    index += 2
                else:
                    index += 1
                continue
            terminator = '"' if state == "string" else "'"
            output.append(" " if mask_literals else char)
            index += 1
            if char == terminator:
                state = "code"
            continue
    return "".join(output)


def extract_method(source: str, name: str) -> str:
    masked = strip_comments(source, mask_literals=True)
    declaration = re.compile(
        r"\b(?:public|private|internal|protected)\s+"
        r"(?:(?:static|override|virtual|sealed|async)\s+)*"
        r"[A-Za-z_][A-Za-z0-9_<>,.?\[\]]*\s+" + re.escape(name) + r"\s*\("
    )
    match = declaration.search(masked)
    if match is None:
        raise ValueError("method declaration not found: " + name)
    opening = masked.find("{", match.end())
    if opening < 0:
        raise ValueError("method body not found: " + name)
    depth = 0
    for index in range(opening, len(masked)):
        if masked[index] == "{":
            depth += 1
        elif masked[index] == "}":
            depth -= 1
            if depth == 0:
                return source[match.start():index + 1]
    raise ValueError("unterminated method body: " + name)


def method_hash(source: str, name: str) -> str:
    method = strip_comments(extract_method(source, name))
    canonical = re.sub(r"\s+", "", method)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def verify() -> None:
    project = PROJECT.read_text(encoding="utf-8")
    compiled = {
        normalize(path)
        for path in re.findall(r'<Compile Include="([^"]+)"', project)
    }
    if compiled != EXPECTED_COMPILED:
        fail("compiled source set changed: " + repr(sorted(compiled)))
    if compiled & FORBIDDEN_SOURCE_NAMES:
        fail("obsolete deferral source is compiled: " + repr(sorted(compiled & FORBIDDEN_SOURCE_NAMES)))

    source = SUBMODULE.read_text(encoding="utf-8-sig")
    masked = strip_comments(source, mask_literals=True)

    forbidden = {
        "MapState tick patch": r"MapState\s*\.\s*(?:OnTick|OnMapModeTick)",
        "Campaign.RealTick patch": r"(?:Campaign\s*\.\s*RealTick|[\"']RealTick[\"'])",
        "Campaign.Tick patch": r"(?:Campaign\s*\.\s*Tick|[\"']CampaignPeriodicEventManager[\"'])",
        "periodic dispatcher patch": r"CampaignEventDispatcher|CampaignPeriodicEventManager",
        "deferred work queue": r"Task\s*\.\s*Run|ThreadPool\s*\.|Queue\s*<\s*Action|ConcurrentQueue",
    }
    for description, pattern in forbidden.items():
        if re.search(pattern, source, flags=re.MULTILINE):
            fail(description + " is forbidden")

    patch_calls = re.findall(r"\b(?:_harmony|harmony)\s*\.\s*Patch\s*\(", masked)
    if len(patch_calls) != 2:
        fail("expected exactly two reviewed Harmony.Patch call sites, found " + str(len(patch_calls)))
    if len(re.findall(r"\b(?:_harmony|harmony)\s*\.\s*UnpatchAll\s*\(", masked)) != 1:
        fail("expected exactly one Harmony.UnpatchAll call")
    if len(re.findall(r"\b_harmony\s*\.\s*Unpatch\s*\(", masked)) != 1:
        fail("expected exactly one targeted Harmony.Unpatch call")
    if len(re.findall(r"\[\s*HarmonyPriority\s*\(", masked)) != 1:
        fail("expected one HarmonyPriority attribute")
    if re.search(r"\[\s*HarmonyPatch\b", masked):
        fail("HarmonyPatch attributes are not allowed")

    prefix = strip_comments(
        extract_method(source, "HiddenPartyVisualPrefix"),
        mask_literals=True,
    )
    if len(re.findall(r"\breturn\s+false\s*;", prefix)) != 1:
        fail("HiddenPartyVisualPrefix must contain exactly one skip return")

    required_strings = (
        '"SandBox.View.Map.PartyVisual"',
        '"SandBox.View.Map.Visuals.MobilePartyVisual"',
        'fullName.StartsWith("TOR_Core.Campaign"',
        "Fully hidden parties resume immediately when visible",
        "records timing only and never skips callbacks",
    )
    for value in required_strings:
        if value not in source:
            fail("required reviewed invariant is missing: " + value)

    for name, expected in EXPECTED_METHOD_HASHES.items():
        actual = method_hash(source, name)
        if actual != expected:
            fail("reviewed method changed: " + name + " expected=" + expected + " actual=" + actual)

    module = ET.fromstring(MODULE_XML.read_text(encoding="utf-8"))
    if module.find("SingleplayerModule") is None:
        fail("SubModule.xml must use SingleplayerModule")
    if module.find("Official") is None:
        fail("SubModule.xml must declare Official")
    dll = module.find("./SubModules/SubModule/DLLName")
    if dll is None or dll.attrib.get("value") != "MapPerfProbe.dll":
        fail("loader filename must be MapPerfProbe.dll")
    if "<AssemblyName>MapPerfProbe</AssemblyName>" not in project:
        fail("project output must be MapPerfProbe.dll")

    print("safety verification passed")


if __name__ == "__main__":
    source_text = SUBMODULE.read_text(encoding="utf-8-sig")
    if "--print-hashes" in sys.argv:
        for method_name in REVIEWED_METHODS:
            print(f'    "{method_name}": "{method_hash(source_text, method_name)}",')
    else:
        verify()
