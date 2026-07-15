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
'''        bool gateSupported = false;
        bool portSupported = false;
        #endregion

        public bool IsPlacingSettlement => settlementVisualEntity != null && applyPending != null;
        public bool IsDeepEdit => deepEdit && currentDeepTarget != null && IsPlacingSettlement && !IsPlacingGate && !IsPlacingPort;
        public bool IsPlacingGate => ghostGateVisualEntity != null && applyPending != null;
        public bool IsPlacingPort => ghostPortVisualEntity != null && applyPending != null;''',
'''        bool gateSupported = false;
        bool portSupported = false;
        private bool gatePlacementActive = false;
        private bool portPlacementActive = false;
        #endregion

        public bool IsPlacingSettlement => settlementVisualEntity != null && applyPending != null && !gatePlacementActive && !portPlacementActive;
        public bool IsDeepEdit => deepEdit && currentDeepTarget != null && IsPlacingSettlement && !IsPlacingGate && !IsPlacingPort;
        public bool IsPlacingGate => gatePlacementActive && applyPending != null;
        public bool IsPlacingPort => portPlacementActive && applyPending != null;''',
"placement phase fields")

replace_once(
'''        public void RefreshVisualSelection()
        {
            deepEditScale = 1f;
            currentDeepTarget = null;

            currentModelOptionIdx -= 1;''',
'''        public void RefreshVisualSelection()
        {
            gatePlacementActive = false;
            portPlacementActive = false;
            ghostGateVisualEntity?.ClearEntity();
            ghostGateVisualEntity = null;
            ghostPortVisualEntity?.ClearEntity();
            ghostPortVisualEntity = null;
            gatePlacementFrame = null;
            portPlacementFrame = null;

            deepEditScale = 1f;
            currentDeepTarget = null;

            currentModelOptionIdx -= 1;''',
"refresh phase reset")

replace_once(
'''        public void StartPortPlacement()
        {
            // Ensure no previous port frame gets used. If a port was opted into and then backed out off, the frame might still exist
            portPlacementFrame = null;''',
'''        public void StartPortPlacement()
        {
            // The gate click has completed. Keep its frame, but leave the gate-placement phase
            // and remove only the temporary gate marker before deciding about a port.
            gatePlacementActive = false;
            ghostGateVisualEntity?.ClearEntity();
            ghostGateVisualEntity = null;

            // Ensure no previous port frame gets used. If a port was opted into and then backed out off, the frame might still exist
            portPlacementFrame = null;''',
"start port phase")

replace_once(
'''                        PlacementSupported = false;
                        ShowPortPosHelp();
                        ShowGhostPortVisualEntity(true);''',
'''                        PlacementSupported = false;
                        portPlacementActive = true;
                        ShowPortPosHelp(forceShow: true);
                        ShowGhostPortVisualEntity(true);''',
"force port phase")

replace_once(
'''                () =>
                {
                    ApplyNow();
                    return;
                }), true, false);''',
'''                () =>
                {
                    InformationManager.HideInquiry();
                    portPlacementActive = false;
                    ApplyNow();
                    return;
                }), true, false);''',
"port no callback")

replace_once(
'''                    PlacementSupported = false;
                    ShowGatePosHelp();
                    ShowGhostGateVisualEntity(true);''',
'''                    PlacementSupported = false;
                    gatePlacementActive = true;
                    portPlacementActive = false;
                    ShowGatePosHelp(forceShow: true);
                    ShowGhostGateVisualEntity(true);''',
"force gate phase")

replace_once(
'''                    var apply = applyPending;
                    apply.Invoke();''',
'''                    gatePlacementActive = false;
                    portPlacementActive = false;
                    var apply = applyPending;
                    apply.Invoke();''',
"consume placement phase")

replace_once(
'''            gateSupported = false;
            portSupported = false;
            ghostGateVisualEntity = null;''',
'''            gateSupported = false;
            portSupported = false;
            gatePlacementActive = false;
            portPlacementActive = false;
            ghostGateVisualEntity = null;''',
"reset placement phases")

source = source.replace(
'''                if (ghostPortVisualEntity != null)
                {''',
'''                if (portPlacementActive)
                {''',
1)
source = source.replace(
'''                    var previous = ghostPortVisualEntity.GetFrame();

                    portPlacementFrame = identity;''',
'''                    portPlacementFrame = identity;''',
1)
source = source.replace(
'''                if (ghostGateVisualEntity != null)
                {''',
'''                if (gatePlacementActive)
                {''',
1)
source = source.replace(
'''                    var previous = ghostGateVisualEntity.GetFrame();

                    gatePlacementFrame = identity;''',
'''                    gatePlacementFrame = identity;''',
1)

replace_once(
'''                if (ghostPortVisualEntity == null)
                {
                    // Cannot place port, going straight to apply
                    ClearEntities();
                    ApplyNow();
                }''',
'''                if (ghostPortVisualEntity == null)
                {
                    // The visual marker is optional. Keep the explicit port phase active so the
                    // mouse position is still tracked and the user can place the port safely.
                    LogManager.EventTracer.Trace("Port marker prefab unavailable; continuing with cursor-only placement");
                }''',
"port marker fallback")

replace_once(
'''                if (ghostGateVisualEntity == null)
                {
                    // Cannot place gate, going straight to apply
                    ClearEntities();
                    ApplyNow();
                }''',
'''                if (ghostGateVisualEntity == null)
                {
                    // The visual marker is optional. Keep the explicit gate phase active so the
                    // mouse position is still tracked and the user can place the gate safely.
                    LogManager.EventTracer.Trace("Gate marker prefab unavailable; continuing with cursor-only placement");
                }''',
"gate marker fallback")

required = [
    "gatePlacementActive = true;",
    "ShowGatePosHelp(forceShow: true);",
    "ShowPortPosHelp(forceShow: true);",
    "cursor-only placement",
    "InformationManager.HideInquiry();\n                    portPlacementActive = false;",
]
for marker in required:
    if marker not in source:
        raise RuntimeError(f"required patched marker missing: {marker}")

path.write_text(source, encoding="utf-8")
