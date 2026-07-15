from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding="utf-8-sig")

old = '''            // Placement phases own every left click. Commit the exact CampaignVec2 delivered by
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

new = '''            // The placement behavior now detects the mouse-release inside the same map tick that
            // calculates the preview frame. This prefix only suppresses Bannerlord's normal map
            // selection/click handling so it cannot select the ghost or newly created settlement.
            if (behaviour.IsPlacingPort || behaviour.IsPlacingGate || behaviour.IsPlacingSettlement)
            {
                return false;
            }

            return true;'''

if old not in source:
    raise RuntimeError("7.6.1 map-click ownership block not found")
source = source.replace(old, new, 1)

if "CommitGatePlacement(intersectionPoint)" in source:
    raise RuntimeError("MapScreen still commits gate clicks")
if "same map tick that" not in source:
    raise RuntimeError("tick-owned suppression marker missing")

path.write_text(source, encoding="utf-8")
