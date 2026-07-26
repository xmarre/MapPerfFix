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
            if (__instance == null || GetMapScene == null)
            {
                return null;
            }

            return GetMapScene.Invoke(__instance, null) as Scene;
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
            }
            else
            {
                StrategicEntityField?.SetValue(visual, entity);
            }
        }

        private static void AssignTownPhysicalEntities(SettlementVisual visual, List<GameEntity> entities)
        {
            if (SetTownPhysicalEntities != null)
            {
                SetTownPhysicalEntities.Invoke(visual, new object[] { entities });
            }
            else
            {
                TownPhysicalEntitiesField?.SetValue(visual, entities);
            }
        }

        private static void AssignCircleLocalFrame(SettlementVisual visual, MatrixFrame frame)
        {
            if (SetCircleLocalFrame != null)
            {
                SetCircleLocalFrame.Invoke(visual, new object[] { frame });
            }
            else
            {
                CircleLocalFrameField?.SetValue(visual, frame);
            }
        }
''',
'accessor helpers')

start_marker = '''        [HarmonyPrefix]\n        [HarmonyPatch(nameof(OnStartup))]\n'''
end_marker = '''\n\n        [HarmonyFinalizer]\n        //[HarmonyPatch(nameof(SettlementVisual.OnStartup))]'''
start = source.index(start_marker)
end = source.index(end_marker, start)
new_method = '''        [HarmonyPrefix]
        [HarmonyPatch(nameof(OnStartup))]
        public static bool OnStartup(ref SettlementVisual __instance, ref Dictionary<int, List<GameEntity>> ____gateBannerEntitiesWithLevels)
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
                if (!isPlayerSettlement && !isOverwrite)
                {
                    return true;
                }
                handlingCustomVisual = true;

                bool hasCircle = false;
                if (!isOverwrite)
                {
                    Scene scene = __instance.MapScene();
                    AssignStrategicEntity(
                        __instance,
                        scene?.GetCampaignEntityWithName(mapEntity.Id)
                        ?? scene?.GetCampaignEntityWithName(settlement.StringId));
                }

                if (__instance.StrategicEntity == null)
                {
                    IMapScene mapSceneWrapper = Campaign.Current?.MapSceneWrapper;
                    Scene scene = __instance.MapScene();
                    string stringId = settlement.StringId;
                    CampaignVec2 position = settlement.Position;

                    GameEntity copiedVisual = CultureVisualHelper.TryCopyCreatedSettlementVisual(
                        scene,
                        stringId,
                        settlement.Position.ToVec2());
                    if (copiedVisual != null)
                    {
                        AssignStrategicEntity(__instance, copiedVisual);
                    }
                    else
                    {
                        mapSceneWrapper?.AddNewEntityToMapScene(stringId, in position);
                        AssignStrategicEntity(
                            __instance,
                            scene?.GetCampaignEntityWithName(mapEntity.Id)
                            ?? scene?.GetCampaignEntityWithName(stringId));
                    }
                }

                if (__instance.StrategicEntity == null)
                {
                    LogManager.Log.Info($"Player settlement visual startup skipped because no strategic entity could be resolved for '{settlement.StringId}'.");
                    return false;
                }

                if (overwriteItem == null)
                {
                    var playerSettlementItem = PlayerSettlementInfo.Instance?.FindSettlement(settlement);
                    ApplySavedVisualTransform(__instance.StrategicEntity, playerSettlementItem?.RotationMat3, playerSettlementItem?.DeepEdits);
                }
                else
                {
                    ApplySavedVisualTransform(__instance.StrategicEntity, overwriteItem.RotationMat3, overwriteItem.DeepEdits);
                }

                if (settlement.IsFortification)
                {
                    List<GameEntity> gameEntities = new List<GameEntity>();
                    __instance.StrategicEntity.GetChildrenRecursive(ref gameEntities);
                    gameEntities.RemoveAll(entity => entity == null);

                    PopulateSiegeEngineFrameListsFromChildren?.Invoke(__instance, new object[] { gameEntities });
                    UpdateDefenderSiegeEntitiesCache?.Invoke(__instance, null);
                    AssignTownPhysicalEntities(__instance, gameEntities.FindAll(entity => entity.HasTag("bo_town")));

                    List<GameEntity> mapInteractionEntities = new List<GameEntity>();
                    Dictionary<int, List<GameEntity>> bannerEntitiesByLevel = new Dictionary<int, List<GameEntity>>()
                    {
                        { 1, new List<GameEntity>() },
                        { 2, new List<GameEntity>() },
                        { 3, new List<GameEntity>() }
                    };

                    foreach (GameEntity gameEntity in gameEntities)
                    {
                        if (gameEntity.HasTag("main_map_city_gate"))
                        {
                            MatrixFrame globalFrame = gameEntity.GetGlobalFrame();
                            NavigationHelper.IsPositionValidForNavigationType(
                                new CampaignVec2(globalFrame.origin.AsVec2, true),
                                MobileParty.NavigationType.Default);
                            mapInteractionEntities.Add(gameEntity);
                        }

                        if (gameEntity.HasTag("map_settlement_circle"))
                        {
                            AssignCircleLocalFrame(__instance, gameEntity.GetGlobalFrame());
                            hasCircle = true;
                            gameEntity.SetVisibilityExcludeParents(false);
                            mapInteractionEntities.Add(gameEntity);
                        }

                        if (!gameEntity.HasTag("map_banner_placeholder"))
                        {
                            continue;
                        }

                        int upgradeLevel = gameEntity.Parent?.GetUpgradeLevelOfEntity() ?? 0;
                        if (upgradeLevel != 0 && bannerEntitiesByLevel.TryGetValue(upgradeLevel, out List<GameEntity> levelEntities))
                        {
                            levelEntities.Add(gameEntity);
                        }
                        else
                        {
                            bannerEntitiesByLevel[1].Add(gameEntity);
                            bannerEntitiesByLevel[2].Add(gameEntity);
                            bannerEntitiesByLevel[3].Add(gameEntity);
                        }
                        mapInteractionEntities.Add(gameEntity);
                    }
                    ____gateBannerEntitiesWithLevels = bannerEntitiesByLevel;

                    List<MatrixFrame> campFrames1 = new List<MatrixFrame>();
                    List<MatrixFrame> campFrames2 = new List<MatrixFrame>();
                    if (Campaign.Current?.MapSceneWrapper != null)
                    {
                        Campaign.Current.MapSceneWrapper.GetSiegeCampFrames(settlement, out campFrames1, out campFrames2);
                    }
                    if (settlement.Town != null)
                    {
                        settlement.Town.BesiegerCampPositions1 = (campFrames1 ?? new List<MatrixFrame>()).ToArray();
                        settlement.Town.BesiegerCampPositions2 = (campFrames2 ?? new List<MatrixFrame>()).ToArray();
                    }

                    foreach (GameEntity interactionEntity in mapInteractionEntities)
                    {
                        interactionEntity.Remove(112);
                    }

                    if (mapEntity.IsSettlement)
                    {
                        foreach (GameEntity child in __instance.StrategicEntity.GetChildren())
                        {
                            if (!child.HasTag("main_map_city_port"))
                            {
                                continue;
                            }
                            MatrixFrame portFrame = child.GetGlobalFrame();
                            NavigationHelper.IsPositionValidForNavigationType(
                                new CampaignVec2(portFrame.origin.AsVec2, false),
                                MobileParty.NavigationType.Naval);
                        }
                    }
                }

                if (!hasCircle)
                {
                    AssignCircleLocalFrame(__instance, MatrixFrame.Identity);
                    MatrixFrame circleLocalFrame = __instance.CircleLocalFrame;
                    Mat3 circleRotation = circleLocalFrame.rotation;
                    if (settlement.IsVillage)
                    {
                        circleRotation.ApplyScaleLocal(1.75f);
                    }
                    else if (settlement.IsTown)
                    {
                        circleRotation.ApplyScaleLocal(5.75f);
                    }
                    else if (settlement.IsCastle)
                    {
                        circleRotation.ApplyScaleLocal(2.75f);
                    }
                    else
                    {
                        circleRotation.ApplyScaleLocal(1.75f);
                    }
                    circleLocalFrame.rotation = circleRotation;
                    AssignCircleLocalFrame(__instance, circleLocalFrame);
                }

                __instance.StrategicEntity.SetVisibilityExcludeParents(mapEntity.IsVisible);
                __instance.StrategicEntity.SetReadyToRender(true);
                __instance.StrategicEntity.SetEntityEnvMapVisibility(false);

                List<GameEntity> childEntities = new List<GameEntity>();
                __instance.StrategicEntity.GetChildrenRecursive(ref childEntities);
                var visualsOfEntities = MapScreen.VisualsOfEntities;
                var engineVisuals = MapScreenPatch.FrameAndVisualOfEngines();
                if (visualsOfEntities != null && !visualsOfEntities.ContainsKey(__instance.StrategicEntity.Pointer))
                {
                    visualsOfEntities.Add(__instance.StrategicEntity.Pointer, __instance);
                }
                foreach (GameEntity childEntity in childEntities)
                {
                    if (childEntity == null || visualsOfEntities == null ||
                        visualsOfEntities.ContainsKey(childEntity.Pointer) ||
                        (engineVisuals != null && engineVisuals.ContainsKey(childEntity.Pointer)))
                    {
                        continue;
                    }
                    visualsOfEntities.Add(childEntity.Pointer, __instance);
                }

                __instance.StrategicEntity.SetAsPredisplayEntity();
                return false;
            }
            catch (System.Exception e)
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

        private static void ApplySavedVisualTransform(GameEntity strategicEntity, Mat3Saveable? rotation, List<DeepTransformEdit> deepEdits)
        {
            if (strategicEntity == null)
            {
                return;
            }

            if (rotation != null)
            {
                MatrixFrame frame = strategicEntity.GetFrame();
                frame.rotation = rotation;
                strategicEntity.SetFrame(ref frame);
            }

            if (deepEdits == null)
            {
                return;
            }

            List<GameEntity> children = new List<GameEntity>();
            strategicEntity.GetChildrenRecursive(ref children);
            foreach (DeepTransformEdit edit in deepEdits)
            {
                if (edit == null || edit.Index >= children.Count)
                {
                    continue;
                }

                GameEntity entity = edit.Index < 0 ? strategicEntity : children[edit.Index];
                if (entity == null)
                {
                    continue;
                }

                MatrixFrame local = entity.GetFrame();
                local.rotation = edit.Transform?.RotationScale != null ? edit.Transform.RotationScale : local.rotation;
                if (edit.Index >= 0)
                {
                    local.origin = edit.Transform?.Position != null ? edit.Transform.Position : local.origin;
                }
                else if (edit.Transform?.Offsets != null)
                {
                    local.origin += edit.Transform.Offsets;
                }
                entity.SetFrame(ref local);
            }

            foreach (DeepTransformEdit edit in deepEdits.AsEnumerable().Reverse().Where(item => item != null && item.IsDeleted && item.Index >= 0))
            {
                if (edit.Index < children.Count)
                {
                    children[edit.Index]?.ClearEntity();
                }
            }
        }
'''
source = source[:start] + new_method + source[end:]
path.write_text(source, encoding='utf-8', newline='\n')
