using System;
using System.Collections.Generic;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BannerlordPlayerSettlement.Patches
{
    public static class CultureVisualHelper
    {
        private const string TorTemplateMarker = "_ToR";
        private const string PlayerSettlementPrefix = "player_settlement_";

        public static GameEntity TryCopyExistingSettlementVisual(
            Scene scene,
            string cultureId,
            int settlementType,
            string templateId,
            string entityId,
            Vec2 position)
        {
            if (scene == null ||
                string.IsNullOrEmpty(cultureId) ||
                !IsTorIdentifier(templateId))
            {
                return null;
            }

            return CopyMatchingVisual(
                scene,
                cultureId,
                settlementType,
                templateId,
                entityId,
                position);
        }

        public static GameEntity TryCopyCreatedSettlementVisual(
            Scene scene,
            string settlementId,
            Vec2 position)
        {
            if (scene == null || !IsTorIdentifier(settlementId))
                return null;

            Settlement target = Settlement.Find(settlementId);
            if (target == null || target.Culture == null)
                return null;

            int settlementType = target.IsTown ? 1 : target.IsVillage ? 2 : target.IsCastle ? 3 : 0;
            if (settlementType == 0)
                return null;

            return CopyMatchingVisual(
                scene,
                target.Culture.StringId,
                settlementType,
                settlementId,
                settlementId,
                position);
        }

        private static GameEntity CopyMatchingVisual(
            Scene scene,
            string cultureId,
            int settlementType,
            string variantKey,
            string entityId,
            Vec2 position)
        {
            var candidates = new List<Settlement>();
            foreach (Settlement settlement in Settlement.All)
            {
                if (!IsMatchingSource(settlement, cultureId, settlementType))
                    continue;

                GameEntity source = scene.GetCampaignEntityWithName(settlement.StringId);
                if (source != null)
                    candidates.Add(settlement);
            }

            if (candidates.Count == 0 && (settlementType == 1 || settlementType == 3))
            {
                foreach (Settlement settlement in Settlement.All)
                {
                    if (!IsSameCultureSource(settlement, cultureId) ||
                        (!settlement.IsTown && !settlement.IsCastle))
                    {
                        continue;
                    }

                    GameEntity source = scene.GetCampaignEntityWithName(settlement.StringId);
                    if (source != null)
                        candidates.Add(settlement);
                }
            }

            if (candidates.Count == 0)
                return null;

            int startIndex = GetVariantIndex(variantKey, candidates.Count);
            for (int offset = 0; offset < candidates.Count; offset++)
            {
                Settlement settlement = candidates[(startIndex + offset) % candidates.Count];
                GameEntity source = scene.GetCampaignEntityWithName(settlement.StringId);
                if (source == null)
                    continue;

                GameEntity copy = GameEntity.CopyFromPrefab(source);
                if (copy == null)
                    continue;

                copy.Name = entityId;
                Vec3 position3D = new Vec3(position.x, position.y, 0f, -1f);
                position3D.z = scene.GetGroundHeightAtPosition(
                    position.ToVec3(0f),
                    (BodyFlags)544321929);
                copy.SetLocalPosition(position3D);
                return copy;
            }

            return null;
        }

        private static bool IsMatchingSource(
            Settlement settlement,
            string cultureId,
            int settlementType)
        {
            if (!IsSameCultureSource(settlement, cultureId))
                return false;

            if (settlementType == 1)
                return settlement.IsTown;
            if (settlementType == 2)
                return settlement.IsVillage;
            if (settlementType == 3)
                return settlement.IsCastle;

            return false;
        }

        private static bool IsSameCultureSource(Settlement settlement, string cultureId)
        {
            if (settlement == null || settlement.Culture == null || !settlement.IsVisible)
                return false;

            if (!string.Equals(
                    settlement.Culture.StringId,
                    cultureId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return string.IsNullOrEmpty(settlement.StringId) ||
                   !settlement.StringId.StartsWith(
                       PlayerSettlementPrefix,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTorIdentifier(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(TorTemplateMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetVariantIndex(string value, int candidateCount)
        {
            if (candidateCount <= 1 || string.IsNullOrEmpty(value))
                return 0;

            const string marker = "_variant_";
            int markerIndex = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
                return 0;

            int start = markerIndex + marker.Length;
            int end = start;
            while (end < value.Length && char.IsDigit(value[end]))
                end++;

            int variant;
            if (end <= start || !int.TryParse(value.Substring(start, end - start), out variant))
                return 0;

            return Math.Max(0, variant - 1) % candidateCount;
        }
    }
}
