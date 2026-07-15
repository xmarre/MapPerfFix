from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")


def replace_once(old: str, new: str, name: str) -> None:
    global source
    if old not in source:
        raise RuntimeError(f"marker not found for {name}")
    source = source.replace(old, new, 1)


replace_once(
    "if (Main.Settings == null || !Main.Settings.AllowGatePosition || !gateSupported)",
    "if (Main.Settings == null || !gateSupported)",
    "force gate phase for fortifications")

replace_once(
'''                        void ConfirmAndApply()
                        {
                            var createPlayerSettlementText = new TextObject("{=player_settlement_04}Build a Town").ToString();''',
'''                        void ConfirmAndApply()
                        {
                            // Final town confirmation is impossible until a real gate position has
                            // been recorded. This is a second line of defence if a map-click prefix
                            // is skipped or another mod changes MapScreen click dispatch.
                            if (gateSupported && gatePlacementFrame == null)
                            {
                                StartGatePlacement();
                                return;
                            }

                            var createPlayerSettlementText = new TextObject("{=player_settlement_04}Build a Town").ToString();''',
    "town gate requirement")

replace_once(
'''                        void ConfirmAndApply()
                        {
                            var createPlayerSettlementText = new TextObject("{=player_settlement_19}Build a Castle").ToString();''',
'''                        void ConfirmAndApply()
                        {
                            if (gateSupported && gatePlacementFrame == null)
                            {
                                StartGatePlacement();
                                return;
                            }

                            var createPlayerSettlementText = new TextObject("{=player_settlement_19}Build a Castle").ToString();''',
    "castle gate requirement")

required = [
    "Final town confirmation is impossible until a real gate position",
    "if (gateSupported && gatePlacementFrame == null)",
    "if (Main.Settings == null || !gateSupported)",
]
for marker in required:
    if marker not in source:
        raise RuntimeError(f"required marker missing: {marker}")

path.write_text(source, encoding="utf-8")
