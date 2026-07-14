from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")

using_marker = "using HarmonyLib;"
using_line = "using BannerlordPlayerSettlement.Behaviours;\n\n"
if using_line.strip() not in source:
    if using_marker not in source:
        raise RuntimeError("Harmony using marker not found")
    source = source.replace(using_marker, using_line + using_marker, 1)

needle = '''            if (_saveLoadInProgress)
            {
                WriteTransitionLog("SaveAndLoad ignored because a transition is already active");
                return;
            }

            string saveName = (string)ActiveSaveSlotNameProp.GetValue(null);'''

replacement = '''            if (_saveLoadInProgress)
            {
                WriteTransitionLog("SaveAndLoad ignored because a transition is already active");
                return;
            }

            // The placement callback and preview entities must not survive into the save/load
            // transition. Leaving applyPending armed allows the same settlement to be finalized
            // again from repeated input or immediately after the campaign reload.
            try
            {
                PlayerSettlementBehaviour? behaviour = PlayerSettlementBehaviour.Instance;
                if (behaviour != null &&
                    (behaviour.IsPlacingSettlement || behaviour.IsPlacingGate || behaviour.IsPlacingPort))
                {
                    WriteTransitionLog("Clearing completed placement state before save");
                    behaviour.Reset();
                }
            }
            catch (Exception e)
            {
                WriteTransitionLog("Failed to clear placement state before save: " + e);
            }

            string saveName = (string)ActiveSaveSlotNameProp.GetValue(null);'''

if needle not in source:
    raise RuntimeError("SaveAndLoad insertion marker not found")
source = source.replace(needle, replacement, 1)

path.write_text(source, encoding="utf-8")
