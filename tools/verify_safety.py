#!/usr/bin/env python3
from __future__ import annotations

from collections import Counter
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

ALLOWED_HARMONY_MUTATIONS = Counter({
    "_harmony.Patch": 1,
    "_harmony.Unpatch": 1,
    "_harmony.UnpatchAll": 1,
})

ALLOWED_ACCESSTOOLS_CALLS = Counter({
    "TypeByName": 1,
    "GetDeclaredMethods": 1,
})

PATCH_METHODS = (
    "OnSubModuleUnloaded",
    "TryInstallVisualPatch",
    "FindSupportedVisualTick",
    "IsPatchableVisualTick",
    "IsCurrentVisualTickSignature",
    "IsLegacyVisualTickSignature",
    "DisableVisualPatchIfCompatibilityChanged",
    "PartyVisualTickPrefix",
)

EXPECTED_PATCH_METHOD_HASHES = {
    "OnSubModuleUnloaded": "255d748f6f5ed3724855eda802445c2f74e956386a1fdb6537dc320919a28c2b",
    "TryInstallVisualPatch": "53fb4d02170d27b333483190a6a13945a34ab8dda09e399726c41e43336c156b",
    "FindSupportedVisualTick": "770122059fd5f0878f169cd29f8557a2b3f959f27e70fa5e6672a6edbb3f0308",
    "IsPatchableVisualTick": "d6f757af7cfc1cc5edba8c67fb7021a184baf2c501e39493157248303b3be69b",
    "IsCurrentVisualTickSignature": "0e45abbcb6e6a53ce140321c5106ae2906ee7dd4b5a7a938fa4ab584a2ef09ef",
    "IsLegacyVisualTickSignature": "49437359cc8702ee48843eb0ccbe6fe38f470cf50eb81905906e8963e358852d",
    "DisableVisualPatchIfCompatibilityChanged": "e10c8d82c371d91c7026ab2f8cc1e262762d6b5946ed7dcf5c1ae0158825b9aa",
    "PartyVisualTickPrefix": "af1d67d772921486343b99b6eaef7c6bc0ee91c22171bc6604d999d61479371e",
}


def fail(message: str) -> None:
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(1)


def normalize(path: str) -> str:
    return path.replace("\\", "/")


def strip_comments(source: str, mask_literals: bool = False) -> str:
    output: list[str] = []
    i = 0
    state = "code"
    while i < len(source):
        ch = source[i]
        nxt = source[i + 1] if i + 1 < len(source) else ""

        if state == "code":
            if ch == "/" and nxt == "/":
                output.extend("  ")
                i += 2
                state = "line_comment"
                continue
            if ch == "/" and nxt == "*":
                output.extend("  ")
                i += 2
                state = "block_comment"
                continue
            if ch == '"':
                output.append(" " if mask_literals else ch)
                i += 1
                state = "string"
                continue
            if ch == "'":
                output.append(" " if mask_literals else ch)
                i += 1
                state = "char"
                continue
            output.append(ch)
            i += 1
            continue

        if state == "line_comment":
            if ch == "\n":
                output.append("\n")
                state = "code"
            else:
                output.append(" ")
            i += 1
            continue

        if state == "block_comment":
            if ch == "*" and nxt == "/":
                output.extend("  ")
                i += 2
                state = "code"
            else:
                output.append("\n" if ch == "\n" else " ")
                i += 1
            continue

        if ch == "\\":
            output.append(" " if mask_literals else ch)
            if i + 1 < len(source):
                output.append(" " if mask_literals else source[i + 1])
                i += 2
            else:
                i += 1
            continue

        terminator = '"' if state == "string" else "'"
        output.append(" " if mask_literals else ch)
        i += 1
        if ch == terminator:
            state = "code"

    return "".join(output)


def extract_method(source: str, method_name: str) -> str:
    masked = strip_comments(source, mask_literals=True)
    declaration = re.compile(
        r"\b(?:public|private|internal|protected)\s+"
        r"(?:(?:static|override|virtual|sealed|async)\s+)*"
        r"[A-Za-z_][A-Za-z0-9_<>,.?\[\]]*\s+"
        + re.escape(method_name)
        + r"\s*\("
    )
    match = declaration.search(masked)
    if match is None:
        raise ValueError(f"method declaration not found: {method_name}")

    open_brace = masked.find("{", match.end())
    if open_brace < 0:
        raise ValueError(f"method body not found: {method_name}")

    depth = 0
    for index in range(open_brace, len(masked)):
        char = masked[index]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return source[match.start():index + 1]

    raise ValueError(f"unterminated method body: {method_name}")


def canonical_method_hash(source: str, method_name: str) -> str:
    method = extract_method(source, method_name)
    canonical = re.sub(r"\s+", "", strip_comments(method))
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


def patch_surface_errors(source: str) -> list[str]:
    errors: list[str] = []
    masked = strip_comments(source, mask_literals=True)

    if re.search(r"\[\s*HarmonyPatch\b", masked):
        errors.append("HarmonyPatch attributes are not allowed")

    priorities = len(re.findall(r"\[\s*HarmonyPriority\s*\(", masked))
    if priorities != 1:
        errors.append(f"expected exactly one HarmonyPriority attribute, found {priorities}")

    mutation_names = (
        "Patch", "PatchAll", "Unpatch", "UnpatchAll", "ReversePatch",
        "CreateProcessor", "CreateClassProcessor", "CreateReversePatcher",
    )
    mutation_pattern = re.compile(
        r"\b(?P<qualifier>[A-Za-z_][A-Za-z0-9_]*(?:\s*\.\s*[A-Za-z_][A-Za-z0-9_]*)*)"
        r"\s*\.\s*(?P<method>" + "|".join(mutation_names) + r")\s*\("
    )
    mutations: Counter[str] = Counter()
    for match in mutation_pattern.finditer(masked):
        qualifier = re.sub(r"\s+", "", match.group("qualifier"))
        call = qualifier + "." + match.group("method")
        mutations[call] += 1
        if call not in ALLOWED_HARMONY_MUTATIONS:
            errors.append(f"unapproved Harmony mutation API: {call}")

    for call, expected in ALLOWED_HARMONY_MUTATIONS.items():
        if mutations[call] != expected:
            errors.append(f"expected {expected} call(s) to {call}, found {mutations[call]}")

    access_calls = Counter(
        re.findall(r"\bAccessTools\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)\s*\(", masked)
    )
    for call in access_calls:
        if call not in ALLOWED_ACCESSTOOLS_CALLS:
            errors.append(f"unapproved AccessTools lookup API: {call}")
    for call, expected in ALLOWED_ACCESSTOOLS_CALLS.items():
        if access_calls[call] != expected:
            errors.append(
                f"expected {expected} AccessTools.{call} call(s), found {access_calls[call]}"
            )

    for match in re.finditer(r"\.\s*GetMethods?\s*\(", masked):
        prefix = masked[max(0, match.start() - 120):match.end()]
        if re.search(r"typeof\s*\(\s*SubModule\s*\)\s*\.\s*GetMethod\s*\($", prefix) is None:
            errors.append("unapproved reflection MethodInfo lookup")

    if re.search(r"\.\s*GetConstructors?\s*\(", masked):
        errors.append("reflection constructor lookup is not allowed")
    if len(re.findall(r"\bnew\s+Harmony\s*\(", masked)) != 1:
        errors.append("expected exactly one Harmony instance construction")
    if len(re.findall(r"\bnew\s+HarmonyMethod\s*\(", masked)) != 1:
        errors.append("expected exactly one HarmonyMethod construction")
    if len(re.findall(r"\bHarmony\s*\.\s*GetPatchInfo\s*\(", masked)) != 1:
        errors.append("expected exactly one Harmony.GetPatchInfo call")

    return errors


def patch_allowlist_errors(source: str) -> list[str]:
    errors = patch_surface_errors(source)
    expected_type_block = re.compile(
        r"PartyVisualTypeNames\s*=\s*\{\s*"
        r'"SandBox\.View\.Map\.PartyVisual"\s*'
        r"\}\s*;",
        re.DOTALL,
    )
    if expected_type_block.search(source) is None:
        errors.append("PartyVisual type allowlist differs from the reviewed target")

    for method_name, expected_hash in EXPECTED_PATCH_METHOD_HASHES.items():
        try:
            actual_hash = canonical_method_hash(source, method_name)
        except ValueError as exc:
            errors.append(str(exc))
            continue
        if actual_hash != expected_hash:
            errors.append(
                f"reviewed patch method changed: {method_name} "
                f"(expected {expected_hash}, got {actual_hash})"
            )

    return errors


def run_regression_fixtures() -> None:
    fixtures = {
        "HarmonyPatch MapState attribute": '[HarmonyPatch(typeof(MapState), "OnTick")]',
        "typeof MapState reflection hook": 'typeof(MapState).GetMethod("OnTick")',
        "multiline AccessTools MapState hook": 'AccessTools.Method(\n typeof(MapState),\n "OnMapModeTick")',
        "unrelated PartyVisual reference": 'var s = "SandBox.View.Map.PartyVisual"; _harmony.Patch(other, prefix: p);',
    }
    for name, fixture in fixtures.items():
        if not patch_allowlist_errors(fixture):
            fail(f"regression fixture was incorrectly accepted: {name}")


def main() -> None:
    project_text = PROJECT.read_text(encoding="utf-8")
    compiled = {
        normalize(path)
        for path in re.findall(r'<Compile Include="([^"]+)"', project_text)
    }
    if compiled != EXPECTED_COMPILED:
        fail(f"compiled source set changed: expected {sorted(EXPECTED_COMPILED)}, got {sorted(compiled)}")
    if compiled & FORBIDDEN_SOURCE_NAMES:
        fail(f"obsolete simulation-deferral source is compiled: {sorted(compiled & FORBIDDEN_SOURCE_NAMES)}")

    for relative in sorted(compiled):
        text = (ROOT / "MapPerfFix" / relative).read_text(encoding="utf-8-sig")
        if relative != "SubModule.cs" and re.search(
            r"\b(?:Harmony|HarmonyMethod|HarmonyPatch|AccessTools)\b", text
        ):
            fail(f"Harmony API surfaced outside reviewed SubModule.cs: {relative}")

    submodule_text = SUBMODULE.read_text(encoding="utf-8")
    errors = patch_allowlist_errors(submodule_text)
    if errors:
        fail("patch allowlist violation(s):\n- " + "\n- ".join(errors))
    if "never skip, defer, replay, coalesce, or reorder authoritative" not in submodule_text:
        fail("authoritative callback invariant is missing")

    run_regression_fixtures()

    if "<AssemblyName>MapPerfProbe</AssemblyName>" not in project_text:
        fail("project output assembly must be MapPerfProbe.dll")
    if "<Version>2.4.2</Version>" not in project_text:
        fail("Harmony compile-time reference must match the supported 2.4.2 API")

    module_root = ET.fromstring(MODULE_XML.read_text(encoding="utf-8"))
    dll_name = module_root.find("./SubModules/SubModule/DLLName")
    if dll_name is None or dll_name.attrib.get("value") != "MapPerfProbe.dll":
        fail("SubModule.xml DLLName must be MapPerfProbe.dll")

    dependencies = {
        node.attrib.get("Id")
        for node in module_root.findall("./DependedModules/DependedModule")
    }
    for required in ("Bannerlord.Harmony", "MCMv5", "Sandbox"):
        if required not in dependencies:
            fail(f"required load-order dependency is missing: {required}")

    print("safety verification passed")


if __name__ == "__main__":
    if "--print-hashes" in sys.argv:
        source = SUBMODULE.read_text(encoding="utf-8")
        for method_name in PATCH_METHODS:
            print(f'    "{method_name}": "{canonical_method_hash(source, method_name)}",')
    else:
        main()
