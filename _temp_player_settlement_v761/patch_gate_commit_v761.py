from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")


def replace_once(old: str, new: str, name: str) -> None:
    global source
    if old not in source:
        raise RuntimeError(f"marker not found for {name}")
    source = source.replace(old, new, 1)


# 7.6.0 forced the gate phase but the generated XML still obeyed an old persisted
# AllowGatePosition setting. That silently discarded the selected frame and used the
# settlement-center fallback. Once the mandatory phase ran, the committed frame must win.
old_condition = "if (gateSupported && Main.Settings!.AllowGatePosition && gatePlacementFrame != null)"
condition_count = source.count(old_condition)
if condition_count != 4:
    raise RuntimeError(f"expected 4 gate serialization conditions, found {condition_count}")
source = source.replace(old_condition, "if (gateSupported && gatePlacementFrame != null)")

# The ToR map does not reliably use one FaceIslandIndex for a contiguous landmass. The old
# comparison made the gate cursor unusable until it happened to cross a matching face.
old_gate_validation = "bool flag = currentFace.IsValid() && isOnLand && currentFace.FaceIslandIndex == MobileParty.MainParty.CurrentNavigationFace.FaceIslandIndex; // Campaign.Current.MapSceneWrapper.AreFacesOnSameIsland(currentFace, MobileParty.MainParty.CurrentNavigationFace, ignoreDisabled: false);"
replace_once(
    old_gate_validation,
    "bool flag = currentFace.IsValid() && isOnLand;",
    "remove brittle gate island validation")

# Spawn the marker at the selected settlement rather than at the player party. This makes the
# phase visually obvious even before the cursor moves.
replace_once(
    '''                Vec2 position2D = MobileParty.MainParty.GetPosition2D;

                string prefabId = GhostGatePrefabId;''',
    '''                Vec2 position2D = settlementPlacementFrame.HasValue
                    ? settlementPlacementFrame.Value.origin.AsVec2
                    : MobileParty.MainParty.GetPosition2D;

                string prefabId = GhostGatePrefabId;''',
    "gate marker initial position")

# StartGatePlacement must not depend on a PlacementSupported value computed during an earlier
# frame. The click itself is committed below and validates its own land/water state.
replace_once(
    '''            if (applyPending != null && mapScreen.SceneLayer.Input.GetIsMouseActive() && PlacementSupported)
            {
                try
                {
                    PlacementSupported = false;
                    gatePlacementActive = true;''',
    '''            if (applyPending != null && mapScreen.SceneLayer.Input.GetIsMouseActive())
            {
                try
                {
                    gatePlacementFrame = null;
                    PlacementSupported = false;
                    gatePlacementActive = true;''',
    "remove stale start-gate readiness dependency")

commit_methods = '''        /// <summary>
        /// Commits the actual map click rather than trusting a placement frame calculated on a
        /// previous camera tick. This removes timing races between cursor movement, OnBeforeTick
        /// and MapScreen.HandleLeftMouseButtonClick.
        /// </summary>
        public bool CommitSettlementPlacement(CampaignVec2 clickPosition)
        {
            if (!IsPlacingSettlement || !clickPosition.IsOnLand)
            {
                return false;
            }

            MatrixFrame frame = settlementPlacementFrame ?? MatrixFrame.Identity;
            frame.origin = clickPosition.AsVec3();
            settlementPlacementFrame = frame;
            SetFrame(settlementVisualEntity, ref frame);

            PlacementSupported = true;
            LogManager.EventTracer.Trace($"Committed settlement position directly: {clickPosition.X}, {clickPosition.Y}");
            StartGatePlacement();
            return true;
        }

        public bool CommitGatePlacement(CampaignVec2 clickPosition)
        {
            if (!IsPlacingGate || !clickPosition.IsOnLand)
            {
                return false;
            }

            MatrixFrame frame = MatrixFrame.Identity;
            frame.origin = clickPosition.AsVec3();
            frame.Scale(new Vec3(0.25f, 0.25f, 0.25f));
            gatePlacementFrame = frame;
            SetFrame(ghostGateVisualEntity, ref frame);

            PlacementSupported = true;
            LogManager.EventTracer.Trace($"Committed gate position directly: {clickPosition.X}, {clickPosition.Y}");
            StartPortPlacement();
            return true;
        }

        public bool CommitPortPlacement(CampaignVec2 clickPosition)
        {
            if (!IsPlacingPort || clickPosition.IsOnLand)
            {
                return false;
            }

            MatrixFrame frame = MatrixFrame.Identity;
            frame.origin = clickPosition.AsVec3();
            if (Game.Current.GameStateManager.ActiveState is MapState mapState && mapState.Handler is MapScreen mapScreen)
            {
                frame.origin.z = mapScreen.MapScene.GetWaterLevelAtPosition(frame.origin.AsVec2, true, false);
            }
            frame.Scale(new Vec3(0.25f, 0.25f, 0.25f));
            portPlacementFrame = frame;
            SetFrame(ghostPortVisualEntity, ref frame);

            PlacementSupported = true;
            LogManager.EventTracer.Trace($"Committed port position directly: {clickPosition.X}, {clickPosition.Y}");
            ApplyNow();
            return true;
        }

'''

replace_once(
    "        public void StartPortPlacement()\n        {",
    commit_methods + "        public void StartPortPlacement()\n        {",
    "insert direct placement commit methods")

required = [
    "CommitSettlementPlacement(CampaignVec2 clickPosition)",
    "CommitGatePlacement(CampaignVec2 clickPosition)",
    "CommitPortPlacement(CampaignVec2 clickPosition)",
    "bool flag = currentFace.IsValid() && isOnLand;",
    "Committed gate position directly",
    "if (gateSupported && gatePlacementFrame != null)",
]
for marker in required:
    if marker not in source:
        raise RuntimeError(f"required patched marker missing: {marker}")

if old_condition in source:
    raise RuntimeError("old AllowGatePosition serialization gate remains")
if old_gate_validation in source:
    raise RuntimeError("old gate FaceIslandIndex validation remains")

path.write_text(source, encoding="utf-8")
