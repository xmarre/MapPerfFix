from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding='utf-8-sig')

marker = '''

        [HarmonyFinalizer]
        //[HarmonyPatch(nameof(SettlementVisual.OnStartup))]
'''
method = '''

        [HarmonyPrefix]
        [HarmonyPatch("SetSettlementLevelVisibility")]
        public static bool SetSettlementLevelVisibility(ref SettlementVisual __instance)
        {
            var mapVisual = __instance as MapEntityVisual<PartyBase>;
            var settlement = mapVisual?.MapEntity?.Settlement;
            if (!CultureVisualHelper.IsTorPlayerSettlement(settlement) || !settlement.IsPlayerBuilt())
            {
                return true;
            }

            // ToR player settlements use a runtime-created strategic entity. Bannerlord's
            // native level-visibility routine recursively toggles physics on every child.
            // That native pass is unsafe for runtime-created campaign visual hierarchies
            // and can raise an AccessViolationException before managed code can recover.
            // The copied prefab already carries its own visibility state; skip only this
            // native physics/level pass for the ToR player-settlement visual.
            return false;
        }
'''
if source.count(marker) != 1:
    raise RuntimeError(f'expected one finalizer insertion marker, found {source.count(marker)}')
source = source.replace(marker, method + marker, 1)
path.write_text(source, encoding='utf-8', newline='\n')
