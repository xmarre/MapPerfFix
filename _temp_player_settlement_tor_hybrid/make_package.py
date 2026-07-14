from __future__ import annotations

import hashlib
import os
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

WORKSPACE = Path(os.environ.get("GITHUB_WORKSPACE", Path.cwd())).resolve()
ROOT = WORKSPACE / "_player_settlement_tor_hybrid_build"
SOURCE = ROOT / "source"
STAGING = ROOT / "package"
OUT = ROOT / "out"
DOWNLOAD = WORKSPACE / "_downloaded_native"
UPSTREAM_COMMIT = "52d86c7480778afb83e476ac742895f73fbf6d7f"


def run(*args: str) -> None:
    print("+", " ".join(args), flush=True)
    subprocess.run(args, check=True)


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def fallback_prefab(template_type: str) -> str:
    if template_type == "Village":
        return "map_icon_full_vlandia_village"
    return "map_icon_castle_vlandia"


def find_first(root: Path, name: str) -> Path:
    matches = list(root.rglob(name))
    if not matches:
        raise FileNotFoundError(f"Could not find {name} below {root}")
    return matches[0]


def clean_copy_module(source_module: Path, target_module: Path) -> None:
    shutil.copytree(source_module, target_module)
    for relative in (
        "Prefabs",
        "ModuleData/Player_Settlement_Templates",
        "ModuleData/Player_Settlement_Templates_War_Sails",
        "bin",
    ):
        p = target_module / relative
        if p.exists():
            shutil.rmtree(p)


def collect_original_prefab_mapping(prefab_dir: Path) -> dict[str, str]:
    mapping: dict[str, str] = {}
    for path in sorted(prefab_dir.glob("*.xml")):
        tree = ET.parse(path)
        root = tree.getroot()
        for logical_root in root.findall("game_entity"):
            logical_id = logical_root.get("name")
            source_root = logical_root.find("./children/game_entity")
            source_id = source_root.get("name") if source_root is not None else None
            if not logical_id or not source_id:
                continue
            old = mapping.get(logical_id)
            if old and old != source_id:
                raise RuntimeError(f"Conflicting prefab mapping for {logical_id}: {old} vs {source_id}")
            mapping[logical_id] = source_id
    if not mapping:
        raise RuntimeError("No ToR prefab source mappings were derived")
    return mapping


def write_module_xml(base_module: Path, tor_module: Path) -> None:
    base_xml = """<Module>
  <Name value="Player Settlement (ToR Hybrid)" />
  <Id value="PlayerSettlement" />
  <Version value="v7.5.4" />
  <Url value="https://www.nexusmods.com/mountandblade2bannerlord/mods/7298" />
  <SingleplayerModule value="true" />
  <MultiplayerModule value="false" />
  <Official value="false" />
  <DefaultModule value="false" />
  <ModuleCategory value="Singleplayer" />
  <ModuleType value="Community" />
  <UpdateInfo value="NexusMods:7298" />
  <PlayerSettlementsTemplatesBlacklist path="ModuleData/template_blacklist.txt" />
  <DependedModules>
    <DependedModule Id="Bannerlord.Harmony" DependentVersion="v2.4.2" />
    <DependedModule Id="Bannerlord.ButterLib" DependentVersion="v2.10.2" />
    <DependedModule Id="Bannerlord.UIExtenderEx" DependentVersion="v2.13.2" />
    <DependedModule Id="Bannerlord.MBOptionScreen" DependentVersion="v5.11.3" />
    <DependedModule Id="Native" DependentVersion="v1.3.15" />
    <DependedModule Id="SandBoxCore" DependentVersion="v1.3.15" />
    <DependedModule Id="Sandbox" DependentVersion="v1.3.15" />
    <DependedModule Id="StoryMode" DependentVersion="v1.3.15" />
  </DependedModules>
  <DependedModuleMetadatas>
    <DependedModuleMetadata id="Bannerlord.Harmony" order="LoadBeforeThis" version="v2.4.2" />
    <DependedModuleMetadata id="Bannerlord.ButterLib" order="LoadBeforeThis" version="v2.10.2" />
    <DependedModuleMetadata id="Bannerlord.UIExtenderEx" order="LoadBeforeThis" version="v2.13.2" />
    <DependedModuleMetadata id="Bannerlord.MBOptionScreen" order="LoadBeforeThis" version="v5.11.3" />
    <DependedModuleMetadata id="Native" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="SandBoxCore" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="Sandbox" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="StoryMode" order="LoadBeforeThis" version="1.0.0.*" />
  </DependedModuleMetadatas>
  <SubModules>
    <SubModule>
      <Name value="PlayerSettlement" />
      <DLLName value="PlayerSettlement.dll" />
      <SubModuleClassType value="BannerlordPlayerSettlement.Main" />
      <Tags />
    </SubModule>
  </SubModules>
  <Xmls />
</Module>
"""
    tor_xml = """<Module>
  <Name value="Player Settlement: The Old Realms (1.16 hybrid scenes)" />
  <Id value="PlayerSettlement_TOR" />
  <Version value="v1.4.0" />
  <Url value="https://www.nexusmods.com/mountandblade2bannerlord/mods/7298" />
  <SingleplayerModule value="true" />
  <MultiplayerModule value="false" />
  <Official value="false" />
  <DefaultModule value="false" />
  <ModuleCategory value="Singleplayer" />
  <ModuleType value="Community" />
  <PlayerSettlementsTemplates path="ModuleData/Player_Settlement_Templates" />
  <DependedModules>
    <DependedModule Id="Bannerlord.Harmony" DependentVersion="v2.4.2" />
    <DependedModule Id="Bannerlord.ButterLib" DependentVersion="v2.10.2" />
    <DependedModule Id="Bannerlord.UIExtenderEx" DependentVersion="v2.13.2" />
    <DependedModule Id="Bannerlord.MBOptionScreen" DependentVersion="v5.11.3" />
    <DependedModule Id="Native" DependentVersion="v1.3.15" />
    <DependedModule Id="SandBoxCore" DependentVersion="v1.3.15" />
    <DependedModule Id="Sandbox" DependentVersion="v1.3.15" />
    <DependedModule Id="StoryMode" DependentVersion="v1.3.15" />
    <DependedModule Id="TOR_Armory" />
    <DependedModule Id="TOR_Environment" />
    <DependedModule Id="TOR_Core" />
    <DependedModule Id="PlayerSettlement" DependentVersion="v7.5.4" />
  </DependedModules>
  <DependedModuleMetadatas>
    <DependedModuleMetadata id="Bannerlord.Harmony" order="LoadBeforeThis" version="v2.4.2" />
    <DependedModuleMetadata id="Bannerlord.ButterLib" order="LoadBeforeThis" version="v2.10.2" />
    <DependedModuleMetadata id="Bannerlord.UIExtenderEx" order="LoadBeforeThis" version="v2.13.2" />
    <DependedModuleMetadata id="Bannerlord.MBOptionScreen" order="LoadBeforeThis" version="v5.11.3" />
    <DependedModuleMetadata id="Native" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="SandBoxCore" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="Sandbox" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="StoryMode" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="TOR_Armory" order="LoadBeforeThis" version="v1.16.0" />
    <DependedModuleMetadata id="TOR_Environment" order="LoadBeforeThis" version="v1.16.0" />
    <DependedModuleMetadata id="TOR_Core" order="LoadBeforeThis" version="v1.16.0" />
    <DependedModuleMetadata id="PlayerSettlement" order="LoadBeforeThis" version="v7.5.4" />
  </DependedModuleMetadatas>
  <SubModules />
  <Xmls />
</Module>
"""
    (base_module / "SubModule.xml").write_text(base_xml, encoding="utf-8")
    (tor_module / "SubModule.xml").write_text(tor_xml, encoding="utf-8")


def main() -> int:
    if ROOT.exists():
        shutil.rmtree(ROOT)
    ROOT.mkdir(parents=True)
    OUT.mkdir(parents=True)

    run("git", "clone", "https://github.com/BOTLANNER/BannerlordPlayerSettlement.git", str(SOURCE))
    run("git", "-C", str(SOURCE), "checkout", UPSTREAM_COMMIT)
    actual = subprocess.check_output(["git", "-C", str(SOURCE), "rev-parse", "HEAD"], text=True).strip()
    if actual != UPSTREAM_COMMIT:
        raise RuntimeError(f"Unexpected upstream commit {actual}")

    dll = find_first(DOWNLOAD, "PlayerSettlement.dll")
    pdb_matches = list(DOWNLOAD.rglob("PlayerSettlement.pdb"))
    pdb = pdb_matches[0] if pdb_matches else None

    base_module = STAGING / "PlayerSettlement"
    tor_module = STAGING / "PlayerSettlement_TOR"
    clean_copy_module(SOURCE / "BannerlordPlayerSettlement" / "_Module", base_module)
    (tor_module / "ModuleData" / "Player_Settlement_Templates").mkdir(parents=True)

    for runtime in ("Win64_Shipping_Client", "Gaming.Desktop.x64_Shipping_Client"):
        runtime_dir = base_module / "bin" / runtime
        runtime_dir.mkdir(parents=True)
        shutil.copy2(dll, runtime_dir / "PlayerSettlement.dll")
        if pdb:
            shutil.copy2(pdb, runtime_dir / "PlayerSettlement.pdb")

    mapping = collect_original_prefab_mapping(SOURCE / "PlayerSettlement_TOR" / "Prefabs")
    report_lines = ["logical_template_id\tdirect_existing_ToR_prefab\ttemplate_type\tscene_ids"]
    source_templates = SOURCE / "PlayerSettlement_TOR" / "ModuleData" / "Player_Settlement_Templates"
    target_templates = tor_module / "ModuleData" / "Player_Settlement_Templates"
    total_settlements = 0
    mapped_settlements = 0

    for source_template in sorted(source_templates.glob("*.xml")):
        tree = ET.parse(source_template)
        root = tree.getroot()
        for settlement in root.findall("Settlement"):
            total_settlements += 1
            logical_id = settlement.get("id", "")
            template_type = settlement.get("template_type", "")
            direct_prefab = mapping.get(logical_id)
            if direct_prefab:
                mapped_settlements += 1
                settlement.set("prefab_id", direct_prefab)
            else:
                direct_prefab = fallback_prefab(template_type)
                settlement.set("prefab_id", direct_prefab)
            scene_ids: list[str] = []
            for location in settlement.findall("./Locations/Location"):
                for key, value in location.attrib.items():
                    if key.startswith("scene_name") and value:
                        scene_ids.append(value)
            report_lines.append(f"{logical_id}\t{direct_prefab}\t{template_type}\t{','.join(scene_ids)}")
        target = target_templates / source_template.name
        ET.indent(tree, space="  ")
        tree.write(target, encoding="utf-8", xml_declaration=True)

    if total_settlements == 0:
        raise RuntimeError("No ToR settlement templates found")
    if mapped_settlements != total_settlements:
        print(f"WARNING: mapped {mapped_settlements}/{total_settlements}; native fallback used for the rest")

    write_module_xml(base_module, tor_module)

    notes = f"""Player Settlement 7.5.4 - ToR Hybrid Scenes build
Bannerlord 1.3.15 / The Old Realms WiTM 1.16

This package keeps the crash-safe 7.5.4 DLL and ships zero custom prefab XML.
It restores the original PlayerSettlement_TOR templates, including ToR town, castle,
village, arena, tavern, keep and other location scene IDs.

Campaign-map visuals use the already-loaded original ToR prefab roots derived from the
upstream compatibility addon instead of duplicated/expanded prefab XML. This avoids the
native startup crash caused by parsing the large custom prefab corpus.

Derived direct ToR prefab mappings: {mapped_settlements}/{total_settlements}

Some original ToR templates intentionally reference a few vanilla fallback scenes where
ToR did not provide a dedicated equivalent (for example some prisons or generic houses).

INSTALLATION: delete Modules\\PlayerSettlement and Modules\\PlayerSettlement_TOR first.
Do not merge this package over an older installation.
"""
    (STAGING / "TOR_HYBRID_README.txt").write_text(notes, encoding="utf-8")
    (STAGING / "DELETE_OLD_MODULE_FOLDERS_FIRST.txt").write_text(
        "Delete Modules\\PlayerSettlement and Modules\\PlayerSettlement_TOR before installing.\n",
        encoding="utf-8",
    )
    (OUT / "tor_prefab_scene_mapping.tsv").write_text("\n".join(report_lines) + "\n", encoding="utf-8")

    for forbidden in STAGING.rglob("Prefabs"):
        raise RuntimeError(f"Forbidden Prefabs directory remains: {forbidden}")
    prefab_xml = [p for p in STAGING.rglob("*.xml") if "prefab" in p.name.lower()]
    if prefab_xml:
        raise RuntimeError(f"Custom prefab XML remains: {prefab_xml}")

    xml_files = list(STAGING.rglob("*.xml"))
    for path in xml_files:
        ET.parse(path)

    dll_hashes = {sha256(p) for p in (base_module / "bin").rglob("PlayerSettlement.dll")}
    if len(dll_hashes) != 1:
        raise RuntimeError("Runtime DLL copies differ")

    archive_base = OUT / "Player_Settlements_7.5.4_BL_1.3.15_ToR_WiTM_1.16_HybridScenes"
    zip_path = Path(shutil.make_archive(str(archive_base), "zip", STAGING))
    (OUT / "SHA256.txt").write_text(
        f"ZIP  {sha256(zip_path)}  {zip_path.name}\nDLL  {next(iter(dll_hashes))}  PlayerSettlement.dll\n",
        encoding="utf-8",
    )
    (OUT / "package_manifest.txt").write_text(
        "\n".join(str(p.relative_to(STAGING)) for p in sorted(STAGING.rglob("*")) if p.is_file()) + "\n",
        encoding="utf-8",
    )
    print(f"Built {zip_path}")
    print(f"Templates: {total_settlements}; direct ToR prefab mappings: {mapped_settlements}")
    print(f"XML validated: {len(xml_files)}")
    print(f"ZIP SHA256: {sha256(zip_path)}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        OUT.mkdir(parents=True, exist_ok=True)
        (OUT / "fatal.txt").write_text(repr(exc), encoding="utf-8")
        raise
