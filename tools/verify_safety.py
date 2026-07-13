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
BOOTSTRAP = ROOT / "MapPerfFix" / "BootstrapSubModule.cs"
ASSEMBLY_INFO = ROOT / "MapPerfFix" / "Properties" / "AssemblyInfo.cs"
VERSION_FILE = ROOT / "version.txt"
MAX_MODULE_XML_BYTES = 64 * 1024

EXPECTED_COMPILED = {
    "BootstrapSubModule.cs",
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


def parse_bounded_module_xml(path: Path) -> ET.Element:
    with path.open("rb") as module_file:
        data = module_file.read(MAX_MODULE_XML_BYTES + 1)
    if len(data) > MAX_MODULE_XML_BYTES:
        fail(
            "SubModule.xml exceeds the " + str(MAX_MODULE_XML_BYTES) +
            " byte verification limit"
        )

    upper = data.upper()
    if b"<!DOCTYPE" in upper or b"<!ENTITY" in upper:
        fail("SubModule.xml must not contain DTD or entity declarations")

    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError as exception:
        fail("SubModule.xml is not valid UTF-8: " + str(exception))

    try:
        parser = ET.XMLParser(target=ET.TreeBuilder())
        return ET.fromstring(text, parser=parser)
    except ET.ParseError as exception:
        fail("SubModule.xml is not valid XML: " + str(exception))


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
    submodule_source = strip_comments(source, mask_literals=False)

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

    bootstrap = BOOTSTRAP.read_text(encoding="utf-8-sig")
    bootstrap_code = strip_comments(bootstrap, mask_literals=True)
    bootstrap_source = strip_comments(bootstrap, mask_literals=False)
    required_bootstrap_literals = (
        '"MapPerfProbe"',
        '"bootstrap.log"',
        '"MapPerfProbe.loaded.txt"',
        '"TaleWorlds.Library.InformationMessage, TaleWorlds.Library"',
        '"TaleWorlds.Library.InformationManager, TaleWorlds.Library"',
    )
    for value in required_bootstrap_literals:
        if value not in bootstrap_source:
            fail("bootstrap invariant is missing: " + value)

    for status_source in (bootstrap_source, submodule_source):
        if "TaleWorlds.Core.InformationMessage" in status_source or \
           "TaleWorlds.Core.InformationManager" in status_source:
            fail("status API must resolve from TaleWorlds.Library")

    status_method = strip_comments(
        extract_method(source, "TryShowStatusMessage"),
        mask_literals=False,
    )
    if "BootstrapSubModule.TryShowStatusMessage" not in status_method:
        fail("main submodule must reuse the bootstrap TaleWorlds.Library status helper")

    if re.search(r"\b(?:MapPerfConfig|MapPerfSettings|Harmony|HarmonyLib)\b", bootstrap_code):
        fail("bootstrap must remain independent of MCM settings and Harmony")

    bootstrap_entry = strip_comments(
        extract_method(bootstrap, "OnSubModuleLoad"),
        mask_literals=False,
    )
    if re.search(
        r'\bTryWriteBootstrapSentinel\s*\(\s*"entered OnSubModuleLoad"\s*\)',
        bootstrap_entry,
    ) is None:
        fail("bootstrap entry sentinel must use the fail-open wrapper")

    bootstrap_screen = strip_comments(
        extract_method(bootstrap, "OnBeforeInitialModuleScreenSetAsRoot"),
        mask_literals=False,
    )
    if "TryShowStatusMessage" not in bootstrap_screen:
        fail("bootstrap must show an unmistakable status on the initial module screen")
    if "Assembly.GetName().Version" not in bootstrap_source:
        fail("bootstrap must derive its displayed version from the compiled assembly")
    if re.search(r'\bVersion\s*=\s*"[0-9]+\.[0-9]+\.[0-9]+"', bootstrap_source):
        fail("bootstrap must not duplicate the authoritative semantic version")

    version = VERSION_FILE.read_text(encoding="utf-8").strip()
    version_pattern = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
    if re.fullmatch(version_pattern, version) is None:
        fail("version.txt must contain canonical MAJOR.MINOR.PATCH")

    assembly_info = ASSEMBLY_INFO.read_text(encoding="utf-8")
    assembly_code = strip_comments(assembly_info, mask_literals=False)
    assembly_version = version + ".0"
    for attribute in ("AssemblyVersion", "AssemblyFileVersion"):
        values = re.findall(
            r'\[\s*assembly\s*:\s*' + attribute +
            r'\s*\(\s*"([^"]+)"\s*\)\s*\]',
            assembly_code,
        )
        if values != [assembly_version]:
            fail(attribute + " is not synchronized with version.txt")

    module = parse_bounded_module_xml(MODULE_XML)
    module_version = module.find("Version")
    if module_version is None or module_version.attrib.get("value") != "v" + version:
        fail("SubModule.xml version is not synchronized with version.txt")

    leading_tags = [child.tag for child in list(module)[:6]]
    expected_leading_tags = [
        "Id",
        "Name",
        "Version",
        "DefaultModule",
        "ModuleCategory",
        "ModuleType",
    ]
    if leading_tags != expected_leading_tags:
        fail(
            "SubModule.xml must use the current Bannerlord element order; got " +
            repr(leading_tags)
        )

    default_module = module.find("DefaultModule")
    module_category = module.find("ModuleCategory")
    module_type = module.find("ModuleType")
    if default_module is None or default_module.attrib.get("value") != "false":
        fail("SubModule.xml must declare DefaultModule=false")
    if module_category is None or module_category.attrib.get("value") != "Singleplayer":
        fail("SubModule.xml must declare ModuleCategory=Singleplayer")
    if module_type is None or module_type.attrib.get("value") != "Community":
        fail("SubModule.xml must declare ModuleType=Community")

    for obsolete in (
        "Singleplayer",
        "Multiplayer",
        "SingleplayerModule",
        "MultiplayerModule",
        "Official",
    ):
        if module.find(obsolete) is not None:
            fail("obsolete loader element is not allowed: " + obsolete)

    dependencies = {
        node.attrib.get("Id")
        for node in module.findall("./DependedModules/DependedModule")
    }
    if "StoryMode" in dependencies:
        fail("StoryMode must not be a hard dependency for TOR sandbox campaigns")
    if "MCMv5" in dependencies:
        fail("obsolete MCM module ID is forbidden; use Bannerlord.MBOptionScreen")

    for dependency in module.findall("./DependedModules/DependedModule"):
        dependent_version = dependency.attrib.get("DependentVersion")
        if dependent_version and re.fullmatch(
            r"v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:\.(?:0|[1-9][0-9]*))?",
            dependent_version,
        ) is None:
            fail(
                "dependency version must be a complete dotted version: " +
                dependency.attrib.get("Id", "<missing>") + "=" + dependent_version
            )
    for required in ("Native", "SandBoxCore", "Sandbox", "Bannerlord.Harmony", "Bannerlord.MBOptionScreen"):
        if required not in dependencies:
            fail("required module dependency is missing: " + required)

    submodules = module.findall("./SubModules/SubModule")
    if len(submodules) != 2:
        fail("SubModule.xml must contain exactly two submodules")
    class_types = []
    for node in submodules:
        class_type = node.find("SubModuleClassType")
        class_types.append(None if class_type is None else class_type.attrib.get("value"))
    expected_class_types = [
        "MapPerfProbe.BootstrapSubModule",
        "MapPerfProbe.SubModule",
    ]
    if class_types != expected_class_types:
        fail(
            "SubModule.xml must load BootstrapSubModule first and SubModule second; got " +
            repr(class_types)
        )
    for node in submodules:
        dll = node.find("DLLName")
        if dll is None or dll.attrib.get("value") != "MapPerfProbe.dll":
            fail("every submodule must load MapPerfProbe.dll")
        if node.find("Assemblies") is None:
            fail("every submodule must contain the current-schema Assemblies element")
        child_tags = [child.tag for child in list(node)]
        if child_tags != ["Name", "DLLName", "SubModuleClassType", "Assemblies", "Tags"]:
            fail("submodule elements are not in current Bannerlord schema order: " + repr(child_tags))

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
