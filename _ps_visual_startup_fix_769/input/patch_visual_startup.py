from pathlib import Path
import sys

path = Path(sys.argv[1])
source = path.read_text(encoding='utf-8-sig')

def replace_once(old: str, new: str, label: str):
    global source
    if old not in source:
        raise RuntimeError(f'marker not found: {label}')
    source = source.replace(old, new, 1)

replace_once(
'''        static MethodInfo SetStrategicEntity = AccessTools.Property(typeof(SettlementVisual), "StrategicEntity").SetMethod;
        static MethodInfo SetTownPhysicalEntities = AccessTools.Property(typeof(SettlementVisual), "TownPhysicalEntities").SetMethod;
        static MethodInfo SetCircleLocalFrame = AccessTools.Property(typeof(SettlementVisual), "CircleLocalFrame").SetMethod;

        static MethodInfo GetMapScene = AccessTools.Property(typeof(SettlementVisual), "MapScene").GetMethod;
''',
'''        static readonly PropertyInfo StrategicEntityProperty = AccessTools.Property(typeof(SettlementVisual), "StrategicEntity");
        static readonly MethodInfo SetStrategicEntity = StrategicEntityProperty?.GetSetMethod(true);
        static readonly FieldInfo StrategicEntityField = AccessTools.Field(typeof(SettlementVisual), "<StrategicEntity>k__BackingField")
            ?? AccessTools.Field(typeof(SettlementVisual), "_strategicEntity");

        static readonly PropertyInfo TownPhysicalEntitiesProperty = AccessTools.Property(typeof(SettlementVisual), "TownPhysicalEntities");
        static readonly MethodInfo SetTownPhysicalEntities = TownPhysicalEntitiesProperty?.GetSetMethod(true);
        static readonly FieldInfo TownPhysicalEntitiesField = AccessTools.Field(typeof(SettlementVisual), "<TownPhysicalEntities>k__BackingField")
            ?? AccessTools.Field(typeof(SettlementVisual), "_townPhysicalEntities");

        static readonly PropertyInfo CircleLocalFrameProperty = AccessTools.Property(typeof(SettlementVisual), "CircleLocalFrame");
        static readonly MethodInfo SetCircleLocalFrame = CircleLocalFrameProperty?.GetSetMethod(true);
        static readonly FieldInfo CircleLocalFrameField = AccessTools.Field(typeof(SettlementVisual), "<CircleLocalFrame>k__BackingField")
            ?? AccessTools.Field(typeof(SettlementVisual), "_circleLocalFrame");

        static readonly MethodInfo GetMapScene = AccessTools.Property(typeof(SettlementVisual), "MapScene")?.GetGetMethod(true);
''',
'accessor hardening')

replace_once(
'''        public static Scene MapScene(this SettlementVisual __instance)
        {
            return (Scene) GetMapScene.Invoke(__instance, null);
        }
''',
'''        public static Scene MapScene(this SettlementVisual __instance)
        {
            if (__instance == null)
            {
                return null;
            }

            if (GetMapScene != null)
            {
                return GetMapScene.Invoke(__instance, null) as Scene;
            }

            return (Campaign.Current?.MapSceneWrapper as MapScene)?.Scene;
        }

        private static void AssignStrategicEntity(SettlementVisual visual, GameEntity entity)
        {
            if (visual == null)
            {
                return;
            }

            if (SetStrategicEntity != null)
            {
                SetStrategicEntity.Invoke(visual, new object[] { entity });
                return;
            }

            StrategicEntityField?.SetValue(visual, entity);
        }

        private static void AssignTownPhysicalEntities(SettlementVisual visual, List<GameEntity> entities)
        {
            if (SetTownPhysicalEntities != null)
            {
                SetTownPhysicalEntities.Invoke(visual, new object[] { entities });
                return;
            }

            TownPhysicalEntitiesField?.SetValue(visual, entities);
        }

        private static void AssignCircleLocalFrame(SettlementVisual visual, MatrixFrame frame)
        {
            if (SetCircleLocalFrame != null)
            {
                SetCircleLocalFrame.Invoke(visual, new object[] { frame });
                return;
            }

            CircleLocalFrameField?.SetValue(visual, frame);
        }
''',
'accessor helpers')

replace_once(
'''        public static bool OnStartup(ref SettlementVisual __instance, ref Dictionary<int, List<GameEntity>> ____gateBannerEntitiesWithLevels)
        {
            try
            {
                OverwriteSettlementItem? overwriteItem = null;
                bool isPlayerSettlement = (__instance.MapEntity != null && __instance.MapEntity.Settlement.IsPlayerBuilt());
                bool isOverwrite = (__instance.MapEntity != null && __instance.MapEntity.Settlement.IsOverwritten(out overwriteItem));
''',
'''        public static bool OnStartup(ref SettlementVisual __instance, ref Dictionary<int, List<GameEntity>> ____gateBannerEntitiesWithLevels)
        {
            bool handlingCustomVisual = false;
            try
            {
                var mapVisual = __instance as MapEntityVisual<PartyBase>;
                var mapEntity = mapVisual?.MapEntity;
                var settlement = mapEntity?.Settlement;
                if (settlement == null)
                {
                    return true;
                }

                OverwriteSettlementItem? overwriteItem = null;
                bool isPlayerSettlement = settlement.IsPlayerBuilt();
                bool isOverwrite = settlement.IsOverwritten(out overwriteItem);
                handlingCustomVisual = isPlayerSettlement || isOverwrite;
''',
'custom visual preconditions')

replace_once(
'''                    SetStrategicEntity.Invoke(__instance, new object[] { __instance.MapScene().GetCampaignEntityWithName(__instance.MapEntity.Id) });
''',
'''                    var scene = __instance.MapScene();
                    AssignStrategicEntity(__instance,
                        scene?.GetCampaignEntityWithName(mapEntity.Id)
                        ?? scene?.GetCampaignEntityWithName(settlement.StringId));
''',
'initial strategic entity assignment')

replace_once(
'''                    IMapScene mapSceneWrapper = Campaign.Current.MapSceneWrapper;
                    string stringId = (__instance as MapEntityVisual<PartyBase>).MapEntity.Settlement.StringId;
                    CampaignVec2 position = (__instance as MapEntityVisual<PartyBase>).MapEntity.Settlement.Position;
                    mapSceneWrapper.AddNewEntityToMapScene(stringId, in position);
                    SetStrategicEntity.Invoke(__instance, new object[] { __instance.MapScene().GetCampaignEntityWithName((__instance as MapEntityVisual<PartyBase>).MapEntity.Id) });
                }
''',
'''                    IMapScene mapSceneWrapper = Campaign.Current?.MapSceneWrapper;
                    string stringId = settlement.StringId;
                    CampaignVec2 position = settlement.Position;
                    mapSceneWrapper?.AddNewEntityToMapScene(stringId, in position);
                    var scene = __instance.MapScene();
                    AssignStrategicEntity(__instance,
                        scene?.GetCampaignEntityWithName(mapEntity.Id)
                        ?? scene?.GetCampaignEntityWithName(stringId));
                }

                if (__instance.StrategicEntity == null)
                {
                    LogManager.Log.Info($"Player settlement visual startup skipped because no strategic entity could be resolved for '{settlement.StringId}'.");
                    return false;
                }
''',
'strategic entity fallback')

source = source.replace('(__instance as MapEntityVisual<PartyBase>).MapEntity.Settlement', 'settlement')
source = source.replace('(__instance as MapEntityVisual<PartyBase>).MapEntity.IsSettlement', 'mapEntity.IsSettlement')
source = source.replace('(__instance as MapEntityVisual<PartyBase>).MapEntity.IsVisible', 'mapEntity.IsVisible')
source = source.replace('__instance.MapEntity.Settlement', 'settlement')

replace_once(
'''                    PopulateSiegeEngineFrameListsFromChildren.Invoke(__instance, new object[] { gameEntities });
                    UpdateDefenderSiegeEntitiesCache.Invoke(__instance, null);
                    SetTownPhysicalEntities.Invoke(__instance, new object[] { gameEntities.FindAll((GameEntity x) => x.HasTag("bo_town")) });
''',
'''                    PopulateSiegeEngineFrameListsFromChildren?.Invoke(__instance, new object[] { gameEntities });
                    UpdateDefenderSiegeEntitiesCache?.Invoke(__instance, null);
                    AssignTownPhysicalEntities(__instance, gameEntities.FindAll((GameEntity x) => x != null && x.HasTag("bo_town")));
''',
'fortification reflection guards')

replace_once(
'''                        int upgradeLevelOfEntity = gameEntity.Parent.GetUpgradeLevelOfEntity();
                        if (upgradeLevelOfEntity != 0)
                        {
                            nums[upgradeLevelOfEntity].Add(gameEntity);
                        }
''',
'''                        int upgradeLevelOfEntity = gameEntity.Parent?.GetUpgradeLevelOfEntity() ?? 0;
                        if (upgradeLevelOfEntity != 0 && nums.TryGetValue(upgradeLevelOfEntity, out var levelEntities))
                        {
                            levelEntities.Add(gameEntity);
                        }
''',
'banner parent and level guard')

replace_once(
'''                        Campaign.Current.MapSceneWrapper.GetSiegeCampFrames(settlement, out matricesFrame, out matricesFrame1);
                        settlement.Town.BesiegerCampPositions1 = matricesFrame.ToArray();
                        settlement.Town.BesiegerCampPositions2 = matricesFrame1.ToArray();
''',
'''                        Campaign.Current?.MapSceneWrapper?.GetSiegeCampFrames(settlement, out matricesFrame, out matricesFrame1);
                        if (settlement.Town != null)
                        {
                            settlement.Town.BesiegerCampPositions1 = (matricesFrame ?? new List<MatrixFrame>()).ToArray();
                            settlement.Town.BesiegerCampPositions2 = (matricesFrame1 ?? new List<MatrixFrame>()).ToArray();
                        }
''',
'siege camp frame null guards')

source = source.replace('SetCircleLocalFrame.Invoke(__instance, new object[] { gameEntity.GetGlobalFrame() });', 'AssignCircleLocalFrame(__instance, gameEntity.GetGlobalFrame());')
source = source.replace('SetCircleLocalFrame.Invoke(__instance, new object[] { MatrixFrame.Identity });', 'AssignCircleLocalFrame(__instance, MatrixFrame.Identity);')
source = source.replace('SetCircleLocalFrame.Invoke(__instance, new object[] { circleLocalFrame });', 'AssignCircleLocalFrame(__instance, circleLocalFrame);')

replace_once(
'''                if (!MapScreen.VisualsOfEntities.ContainsKey(__instance.StrategicEntity.Pointer))
                {
                    MapScreen.VisualsOfEntities.Add(__instance.StrategicEntity.Pointer, __instance);
                }
                foreach (GameEntity gameEntity2 in gameEntities2)
                {
                    if (MapScreen.VisualsOfEntities.ContainsKey(gameEntity2.Pointer) || MapScreenPatch.FrameAndVisualOfEngines().ContainsKey(gameEntity2.Pointer))
                    {
                        continue;
                    }
                    MapScreen.VisualsOfEntities.Add(gameEntity2.Pointer, __instance);
                }
''',
'''                var visualsOfEntities = MapScreen.VisualsOfEntities;
                var engineVisuals = MapScreenPatch.FrameAndVisualOfEngines();
                if (visualsOfEntities != null && !visualsOfEntities.ContainsKey(__instance.StrategicEntity.Pointer))
                {
                    visualsOfEntities.Add(__instance.StrategicEntity.Pointer, __instance);
                }
                foreach (GameEntity gameEntity2 in gameEntities2)
                {
                    if (gameEntity2 == null || visualsOfEntities == null ||
                        visualsOfEntities.ContainsKey(gameEntity2.Pointer) ||
                        (engineVisuals != null && engineVisuals.ContainsKey(gameEntity2.Pointer)))
                    {
                        continue;
                    }
                    visualsOfEntities.Add(gameEntity2.Pointer, __instance);
                }
''',
'map visual dictionary guards')

replace_once(
'''            catch (System.Exception e) { LogManager.Log.NotifyBad(e); }

            return true;
        }
''',
'''            catch (System.Exception e)
            {
                if (handlingCustomVisual)
                {
                    LogManager.Log.SilentException(e);
                    return false;
                }

                LogManager.Log.NotifyBad(e);
                return true;
            }
        }
''',
'custom startup fail closed')

path.write_text(source, encoding='utf-8')
