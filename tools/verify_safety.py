#!/usr/bin/env python3
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "MapPerfFix" / "MapPerfFix.csproj"
MODULE_XML = ROOT / "MapPerfFix" / "SubModule.xml"

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

FORBIDDEN_CODE_PATTERNS = {
    "authoritative MapState patch": r'AccessTools\.(?:Method|TypeByName)\s*\([^\n]*(?:MapState|CampaignEventDispatcher|CampaignPeriodicEventManager)',
    "campaign RealTick patch": r'Campaign\s*\.\s*RealTick|["\']RealTick["\']',
    "campaign event deferral": r'\b(?:OnDailyTick_Prefix|OnHourlyTick_Prefix|OnWeeklyTick_Prefix|DeferPeriodic|DesyncPrefix)\b',
    "background callback queue": r'\b(?:Task\.Run|ThreadPool\.|ConcurrentQueue<|Queue<Action>)',
}


def fail(message: str) -> None:
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(1)


def normalize(path: str) -> str:
    return path.replace("\\", "/")


project_text = PROJECT.read_text(encoding="utf-8")
compiled = {normalize(path) for path in re.findall(r'<Compile Include="([^"]+)"', project_text)}
if compiled != EXPECTED_COMPILED:
    fail(f"compiled source set changed: expected {sorted(EXPECTED_COMPILED)}, got {sorted(compiled)}")

if compiled & FORBIDDEN_SOURCE_NAMES:
    fail(f"obsolete simulation-deferral source is compiled: {sorted(compiled & FORBIDDEN_SOURCE_NAMES)}")

for relative in sorted(compiled):
    path = ROOT / "MapPerfFix" / relative
    text = path.read_text(encoding="utf-8-sig")
    for description, pattern in FORBIDDEN_CODE_PATTERNS.items():
        if re.search(pattern, text, flags=re.MULTILINE):
            fail(f"{description} found in compiled source {path.relative_to(ROOT)}")

submodule_text = (ROOT / "MapPerfFix" / "SubModule.cs").read_text(encoding="utf-8")
if "SandBox.View.Map.PartyVisual" not in submodule_text:
    fail("the only intended rendering patch target is missing")
if "never skip, defer, replay, coalesce, or reorder authoritative" not in submodule_text:
    fail("authoritative callback invariant is missing")

if "<AssemblyName>MapPerfFix</AssemblyName>" not in project_text:
    fail("project output assembly must be MapPerfFix.dll")
if "<Version>2.4.2</Version>" not in project_text:
    fail("Harmony compile-time reference must match the supported 2.4.2 API")

module_root = ET.fromstring(MODULE_XML.read_text(encoding="utf-8"))
dll_name = module_root.find("./SubModules/SubModule/DLLName")
if dll_name is None or dll_name.attrib.get("value") != "MapPerfFix.dll":
    fail("SubModule.xml DLLName does not match the project output")

dependencies = {
    node.attrib.get("Id")
    for node in module_root.findall("./DependedModules/DependedModule")
}
for required in ("Bannerlord.Harmony", "MCMv5", "Sandbox"):
    if required not in dependencies:
        fail(f"required load-order dependency is missing: {required}")

print("safety verification passed")
