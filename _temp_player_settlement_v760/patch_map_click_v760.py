from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")

old = '''            if (__instance.SceneLayer.Input.GetIsMouseActive() && PlayerSettlementBehaviour.Instance != null && PlayerSettlementBehaviour.Instance.IsPlacingPort && !intersectionPoint.IsOnLand && mouseOverFaceIndex.IsValid() && PlayerSettlementBehaviour.Instance.PlacementSupported)
            {
                PlayerSettlementBehaviour.Instance.ApplyNow();
                return false;
            }
            else if (__instance.SceneLayer.Input.GetIsMouseActive() && PlayerSettlementBehaviour.Instance != null && PlayerSettlementBehaviour.Instance.IsPlacingGate && intersectionPoint.IsOnLand && PlayerSettlementBehaviour.Instance.PlacementSupported)
            {
                PlayerSettlementBehaviour.Instance.StartPortPlacement();
                return false;
            }
            else if (__instance.SceneLayer.Input.GetIsMouseActive() && PlayerSettlementBehaviour.Instance != null && PlayerSettlementBehaviour.Instance.IsPlacingSettlement && intersectionPoint.IsOnLand && PlayerSettlementBehaviour.Instance.PlacementSupported)
            {
                if (PlayerSettlementBehaviour.Instance.IsDeepEdit)
                {
                    // TODO: Determine if raycast could select a part?
                    return false;
                }
                PlayerSettlementBehaviour.Instance.StartGatePlacement();
                return false;
            }
            return true;'''

new = '''            PlayerSettlementBehaviour behaviour = PlayerSettlementBehaviour.Instance;
            if (!__instance.SceneLayer.Input.GetIsMouseActive() || behaviour == null)
            {
                return true;
            }

            // Placement phases own every left click. Do not let the vanilla map handler select the
            // ghost settlement or newly created town when a phase is active. OnBeforeTick already
            // validates land/water/navigation and exposes the result through PlacementSupported.
            if (behaviour.IsPlacingPort)
            {
                if (behaviour.PlacementSupported)
                {
                    behaviour.ApplyNow();
                }
                return false;
            }

            if (behaviour.IsPlacingGate)
            {
                if (behaviour.PlacementSupported)
                {
                    behaviour.StartPortPlacement();
                }
                return false;
            }

            if (behaviour.IsPlacingSettlement)
            {
                if (!behaviour.IsDeepEdit && behaviour.PlacementSupported)
                {
                    behaviour.StartGatePlacement();
                }
                return false;
            }

            return true;'''

if old not in source:
    raise RuntimeError("MapScreen placement click body marker not found")
source = source.replace(old, new, 1)

required = [
    "Placement phases own every left click",
    "if (behaviour.IsPlacingGate)",
    "behaviour.StartGatePlacement();",
    "behaviour.StartPortPlacement();",
]
for marker in required:
    if marker not in source:
        raise RuntimeError(f"required marker missing: {marker}")

path.write_text(source, encoding="utf-8")
