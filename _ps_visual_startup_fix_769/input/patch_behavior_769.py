from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding='utf-8-sig')

def replace_once(old: str, new: str, label: str):
    global source
    count = source.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 marker, found {count}')
    source = source.replace(old, new, 1)

replace_once(
'''                Exception addError = null;
                settlementVisualEntity = Campaign.Current.MapSceneWrapper.AddPrefabEntityToMapScene(ref mapScene, ref entityId, ref position2D, ref prefabId, (ex) =>
                {
                    addError = ex;
                });
''',
'''                Exception addError = null;
                settlementVisualEntity = CultureVisualHelper.TryCopyExistingSettlementVisual(
                    mapScene,
                    template.Culture,
                    template.Type,
                    template.Id,
                    entityId,
                    position2D);

                if (settlementVisualEntity == null)
                {
                    settlementVisualEntity = Campaign.Current.MapSceneWrapper.AddPrefabEntityToMapScene(ref mapScene, ref entityId, ref position2D, ref prefabId, (ex) =>
                    {
                        addError = ex;
                    });
                }
''',
'exact-culture visual fallback')

replace_once(
'''                if (settlementVisualEntity == null)
                {
                    Reset();
                }
                settlementVisualEntityChildren.Clear();
''',
'''                if (settlementVisualEntity == null)
                {
                    LogManager.EventTracer.Trace($"Settlement visual prefab returned null: {prefabId}");
                    // A failed visual may not advance into another culture. The TOR-specific
                    // helper already attempted an exact-culture live visual before this branch.
                    Reset();
                    return;
                }
                settlementVisualEntityChildren.Clear();
''',
'null visual cancellation')

replace_once(
'''                if (retry)
                {
                    // Retry once without allowing another retry to avoid stackoverflow loops
                    UpdateSettlementVisualEntity(forward, retry: false);
                }
                else if ((e is AccessViolationException || previousVisualUpdateException is AccessViolationException))
''',
'''                if ((e is AccessViolationException || previousVisualUpdateException is AccessViolationException))
''',
'cross-culture retry removal')

replace_once(
'''                        castleSettlement.Town.OwnerClan = Hero.MainHero.Clan;
''',
'''                        castleSettlement.Town.OwnerClan = null;
                        castleSettlement.Town.OwnerClan = Clan.PlayerClan;
''',
'castle ownership lifecycle')

replace_once(
'''                        townSettlement.Town.OwnerClan = Hero.MainHero.Clan;
''',
'''                        townSettlement.Town.OwnerClan = null;
                        townSettlement.Town.OwnerClan = Clan.PlayerClan;
''',
'town ownership lifecycle')

path.write_text(source, encoding='utf-8', newline='\n')
