from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")


def replace_once(old: str, new: str, name: str) -> None:
    global source
    if old not in source:
        raise RuntimeError(f"marker not found for {name}")
    source = source.replace(old, new, 1)

# Add private runtime setter access and a raw diagnostic path independent of ButterLib/BEW.
replace_once(
'''        static FastInvokeHandler FillGarrisonPartyOnNewGameInvoker = MethodInvoker.GetHandler(AccessTools.Method(typeof(GarrisonTroopsCampaignBehavior), "FillGarrisonPartyOnNewGame"));''',
'''        static FastInvokeHandler FillGarrisonPartyOnNewGameInvoker = MethodInvoker.GetHandler(AccessTools.Method(typeof(GarrisonTroopsCampaignBehavior), "FillGarrisonPartyOnNewGame"));
        private static readonly System.Reflection.MethodInfo? SettlementGatePositionSetter = AccessTools.PropertySetter(typeof(Settlement), nameof(Settlement.GatePosition));
        private static readonly System.Reflection.FieldInfo? SettlementGatePositionField = AccessTools.Field(typeof(Settlement), "<GatePosition>k__BackingField");''',
"runtime gate reflection handles")

# Gate startup must be instant. Loading another campaign-map prefab was the long pause in prior builds.
replace_once(
'''                    ShowGatePosHelp(forceShow: true);
                    ShowGhostGateVisualEntity(true);
                    return;''',
'''                    ShowGatePosHelp(forceShow: true);
                    InformationManager.DisplayMessage(new InformationMessage("GATE PLACEMENT ACTIVE - click the desired settlement entry point."));
                    WriteGateDiagnostic("Gate phase started immediately; marker prefab loading intentionally skipped.");
                    return;''',
"instant marker-free gate phase")

# Make the phase message unambiguous even when the old localized text is easy to miss.
replace_once(
'''            TextObject gatePosMessage = new TextObject("{=player_settlement_36}Choose your gate position. \\r\\nPress {HELP_KEY} for help. \\r\\nClick {MOUSE_CLICK} anywhere to apply or press {ESC_KEY} to go back to settlement placement.");''',
'''            TextObject gatePosMessage = new TextObject("{=!}GATE PLACEMENT ACTIVE\\r\\nMove the cursor to the desired entry point and left-click once.\\r\\nPress {HELP_KEY} for help or {ESC_KEY} to return to settlement placement.");''',
"explicit gate help")

# Port click is committed in the same tick that calculated the preview frame.
replace_once(
'''                    mapScreen.SceneLayer.ActiveCursor = (flag ? CursorType.Default : CursorType.Disabled);
                    PlacementSupported = flag;

                    return;
                }

                if (gatePlacementActive)''',
'''                    mapScreen.SceneLayer.ActiveCursor = (flag ? CursorType.Default : CursorType.Disabled);
                    PlacementSupported = flag;

                    if (flag && mapScreen.SceneLayer.Input.IsKeyReleased(InputKey.LeftMouseButton))
                    {
                        portPlacementFrame = identity;
                        WriteGateDiagnostic($"Port committed in placement tick: {identity.origin.x}, {identity.origin.y}");
                        ApplyNow();
                    }
                    return;
                }

                if (gatePlacementActive)''',
"tick-owned port click")

# Gate click is committed in the exact preview tick, then the workflow advances.
replace_once(
'''                    mapScreen.SceneLayer.ActiveCursor = (flag ? CursorType.Default : CursorType.Disabled);
                    PlacementSupported = flag;

                    return;
                }


                var deepEditChanged''',
'''                    mapScreen.SceneLayer.ActiveCursor = (flag ? CursorType.Default : CursorType.Disabled);
                    PlacementSupported = flag;

                    if (flag && mapScreen.SceneLayer.Input.IsKeyReleased(InputKey.LeftMouseButton))
                    {
                        gatePlacementFrame = identity;
                        InformationManager.DisplayMessage(new InformationMessage($"Gate position set at {identity.origin.x:0.00}, {identity.origin.y:0.00}."));
                        WriteGateDiagnostic($"Gate committed in placement tick: {identity.origin.x}, {identity.origin.y}");
                        StartPortPlacement();
                    }
                    return;
                }


                var deepEditChanged''',
"tick-owned gate click")

# The settlement click is also committed by the preview loop. Remove the base-game island-index
# assumption, which is not reliable on the ToR map.
replace_once(
'''                        bool flag = currentFace.IsValid() && currentFace.FaceIslandIndex == MobileParty.MainParty.CurrentNavigationFace.FaceIslandIndex; // Campaign.Current.MapSceneWrapper.AreFacesOnSameIsland(currentFace, MobileParty.MainParty.CurrentNavigationFace, ignoreDisabled: false);
                        mapScreen.SceneLayer.ActiveCursor = (flag ? CursorType.Default : CursorType.Disabled);
                        PlacementSupported = flag;''',
'''                        bool flag = currentFace.IsValid() && isOnLand;
                        mapScreen.SceneLayer.ActiveCursor = (flag ? CursorType.Default : CursorType.Disabled);
                        PlacementSupported = flag;

                        if (flag && mapScreen.SceneLayer.Input.IsKeyReleased(InputKey.LeftMouseButton))
                        {
                            settlementPlacementFrame = settlementVisualEntity.GetFrame();
                            WriteGateDiagnostic($"Settlement committed in placement tick: {settlementPlacementFrame.Value.origin.x}, {settlementPlacementFrame.Value.origin.y}");
                            StartGatePlacement();
                            return;
                        }''',
"tick-owned settlement click")

# Insert diagnostics and a forced private-property assignment. Settlement.Deserialize reads gate_pos,
# but this verifies and repairs the live object before any party visual or movement can cache it.
helper = '''        private static void WriteGateDiagnostic(string message)
        {
            try
            {
                string directory = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Mount and Blade II Bannerlord", "Configs", "BannerlordPlayerSettlement");
                System.IO.Directory.CreateDirectory(directory);
                System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "gate_placement.log"),
                    $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostics must never affect settlement creation.
            }
        }

        private static void ForceRuntimeGatePosition(Settlement settlement, Vec2 expectedGate)
        {
            CampaignVec2 value = new CampaignVec2(expectedGate, true);
            try
            {
                if (SettlementGatePositionSetter != null)
                {
                    SettlementGatePositionSetter.Invoke(settlement, new object[] { value });
                }
                else if (SettlementGatePositionField != null)
                {
                    SettlementGatePositionField.SetValue(settlement, value);
                }
                else
                {
                    throw new MissingMemberException(typeof(Settlement).FullName, nameof(Settlement.GatePosition));
                }

                Vec2 actual = settlement.GatePosition.ToVec2();
                float error = (actual - expectedGate).Length;
                WriteGateDiagnostic($"Runtime gate applied to {settlement.StringId}: expected={expectedGate.X},{expectedGate.Y}; actual={actual.X},{actual.Y}; error={error}");
                if (error > 0.05f && SettlementGatePositionField != null)
                {
                    SettlementGatePositionField.SetValue(settlement, value);
                    actual = settlement.GatePosition.ToVec2();
                    WriteGateDiagnostic($"Runtime gate backing-field fallback: actual={actual.X},{actual.Y}");
                }
            }
            catch (Exception e)
            {
                WriteGateDiagnostic($"Runtime gate assignment failed for {settlement?.StringId}: {e}");
                LogManager.Log.NotifyBad(e);
            }
        }

'''
replace_once(
"        private Settlement CreateTown(string settlementName, CultureObject culture, out PlayerSettlementItem townItem)\n        {",
helper + "        private Settlement CreateTown(string settlementName, CultureObject culture, out PlayerSettlementItem townItem)\n        {",
"runtime gate helper insertion")

replace_once(
'''            var townSettlement = MBObjectManager.Instance.GetObject<Settlement>(townItem.StringId);
            townItem.Settlement = townSettlement;

            return townSettlement;''',
'''            var townSettlement = MBObjectManager.Instance.GetObject<Settlement>(townItem.StringId);
            townItem.Settlement = townSettlement;
            ForceRuntimeGatePosition(townSettlement, gPos);

            return townSettlement;''',
"force gate after XML object load")

replace_once(
'''                        townSettlement.OnGameCreated();
                        townSettlement.AfterInitialized();
                        townSettlement.OnFinishLoadState();

                        var town = townSettlement.Town;''',
'''                        townSettlement.OnGameCreated();
                        townSettlement.AfterInitialized();
                        townSettlement.OnFinishLoadState();
                        if (gatePlacementFrame != null)
                        {
                            ForceRuntimeGatePosition(townSettlement, gatePlacementFrame.Value.origin.AsVec2);
                        }

                        var town = townSettlement.Town;''',
"force gate after settlement initialization")

required = [
    "Gate phase started immediately; marker prefab loading intentionally skipped.",
    "Gate committed in placement tick",
    "Settlement committed in placement tick",
    "ForceRuntimeGatePosition(townSettlement, gPos)",
    "gate_placement.log",
    "bool flag = currentFace.IsValid() && isOnLand;",
]
for marker in required:
    if marker not in source:
        raise RuntimeError(f"required v7.6.2 marker missing: {marker}")

path.write_text(source, encoding="utf-8")
