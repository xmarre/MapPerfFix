from pathlib import Path

path = Path(__file__).with_name("make_package.py")
source = path.read_text(encoding="utf-8")

old_mapping = '''            source_root = logical_root.find("./children/game_entity")
            source_id = source_root.get("name") if source_root is not None else None
'''
new_mapping = '''            source_root = logical_root.find("./children/game_entity")
            source_id = logical_root.get("old_prefab_name") or (
                source_root.get("name") if source_root is not None else None
            )
'''
if old_mapping not in source:
    raise RuntimeError("Prefab-source mapping block not found")
source = source.replace(old_mapping, new_mapping, 1)

old_lookup = '''            direct_prefab = mapping.get(logical_id)
            if direct_prefab:
'''
new_lookup = '''            direct_prefab = mapping.get(logical_id)
            if not direct_prefab and "{{OWNER_TYPE}}" in logical_id:
                owner_candidates = {
                    mapping.get(logical_id.replace("{{OWNER_TYPE}}", owner_type))
                    for owner_type in ("town", "castle")
                }
                owner_candidates.discard(None)
                if len(owner_candidates) == 1:
                    direct_prefab = owner_candidates.pop()
                elif len(owner_candidates) > 1:
                    print(
                        f"WARNING: town/castle village prefabs differ for {logical_id}: "
                        f"{sorted(owner_candidates)}; using native fallback"
                    )
            if direct_prefab:
'''
if old_lookup not in source:
    raise RuntimeError("Template prefab lookup block not found")
source = source.replace(old_lookup, new_lookup, 1)

path.write_text(source, encoding="utf-8")
print("Patched hybrid builder to use old_prefab_name and generic village mappings")
