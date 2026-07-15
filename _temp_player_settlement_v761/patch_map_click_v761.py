from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")

old = '''            // Placement phases own every left click. Do not let the vanilla map handler select the
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

new = '''            // Placement phases own every left click. Commit the exact CampaignVec2 delivered by
            // MapScreen instead of relying on a frame calculated during an earlier OnBeforeTick.
            // This makes settlement -> gate -> port progression immediate and deterministic.
            if (behaviour.IsPlacingPort)
            {
                behaviour.CommitPortPlacement(intersectionPoint);
                return false;
            }

            if (behaviour.IsPlacingGate)
            {
                behaviour.CommitGatePlacement(intersectionPoint);
                return false;
            }

            if (behaviour.IsPlacingSettlement)
            {
                if (!behaviour.IsDeepEdit)
                {
                    behaviour.CommitSettlementPlacement(intersectionPoint);
                }
                return false;
            }

            return true;'''

if old not in source:
    raise RuntimeError("7.6.0 map click body marker not found")
source = source.replace(old, new, 1)

required = [
    "CommitSettlementPlacement(intersectionPoint)",
    "CommitGatePlacement(intersectionPoint)",
    "CommitPortPlacement(intersectionPoint)",
    "immediate and deterministic",
]
for marker in required:
    if marker not in source:
        raise RuntimeError(f"required map-click marker missing: {marker}")

path.write_text(source, encoding="utf-8")
