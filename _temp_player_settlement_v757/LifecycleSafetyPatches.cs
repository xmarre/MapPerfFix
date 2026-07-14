using System;

using BannerlordPlayerSettlement.Behaviours;

using HarmonyLib;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BannerlordPlayerSettlement.Patches
{
    /// <summary>
    /// Campaign TickEvent can fire during the short hand-off where a loaded Campaign exists,
    /// but Settlement.CurrentSettlement still dereferences an incomplete encounter manager.
    /// The base Tick method only needs to run after those campaign singletons are ready.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSettlementBehaviour), nameof(PlayerSettlementBehaviour.Tick))]
    internal static class PlayerSettlementBehaviourTickPostLoadGuardPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (Game.Current == null || Campaign.Current == null || Hero.MainHero == null)
            {
                return false;
            }

            try
            {
                _ = Settlement.CurrentSettlement;
            }
            catch (NullReferenceException)
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// ApplyNow opens a confirmation inquiry but leaves its callback armed. Repeated map input
    /// could therefore open/invoke the same placement flow more than once. Ignore placement
    /// application while any inquiry is already active; cancellation naturally re-enables it.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSettlementBehaviour), nameof(PlayerSettlementBehaviour.ApplyNow))]
    internal static class PlayerSettlementBehaviourApplyNowInquiryGuardPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            try
            {
                return !InformationManager.IsAnyInquiryActive();
            }
            catch (NullReferenceException)
            {
                return false;
            }
        }
    }
}
