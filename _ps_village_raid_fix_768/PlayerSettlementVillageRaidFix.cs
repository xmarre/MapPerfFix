using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

[assembly: AssemblyVersion("7.6.8.0")]
[assembly: AssemblyFileVersion("7.6.8.0")]

namespace PlayerSettlementVillageRaidFix
{
    public sealed class SubModule : MBSubModuleBase
    {
        private const string HarmonyId = "playersettlement.village.raid.fix.7.6.8";
        private static bool _installed;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (_installed)
                return;

            MethodInfo raidStart = AccessTools.Method(
                typeof(ChangeVillageStateAction),
                nameof(ChangeVillageStateAction.ApplyBySettingToBeingRaided),
                new[] { typeof(Settlement), typeof(MobileParty) });
            if (raidStart == null)
                throw new MissingMethodException(
                    typeof(ChangeVillageStateAction).FullName,
                    "ApplyBySettingToBeingRaided(Settlement, MobileParty)");

            MethodInfo prefix = AccessTools.Method(
                typeof(SubModule),
                nameof(BeforeApplyBySettingToBeingRaided));

            new Harmony(HarmonyId).Patch(raidStart, prefix: new HarmonyMethod(prefix));
            _installed = true;
        }

        public static void BeforeApplyBySettingToBeingRaided(Settlement __0, MobileParty __1)
        {
            Settlement settlement = __0;
            if (!IsPlayerSettlementVillage(settlement))
                return;

            try
            {
                RepairRaidTarget(settlement);
            }
            catch (Exception ex)
            {
                Debug.Print("[PlayerSettlementVillageRaidFix] Raid-target repair failed for " +
                            settlement.StringId + ": " + ex);
            }
        }

        private static void RepairRaidTarget(Settlement settlement)
        {
            Village village = settlement.Village;
            if (village == null)
                return;

            // Dynamic XML loading normally creates the settlement party before a village can be
            // selected as a raid target. Restore only the component-owner link when that native
            // link is absent. No campaign-load scan or unrelated settlement mutation is performed.
            if (village.Owner == null && settlement.Party != null)
            {
                MethodInfo ownerSetter = typeof(SettlementComponent)
                    .GetProperty("Owner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetSetMethod(true);
                ownerSetter?.Invoke(village, new object[] { settlement.Party });
            }

            Settlement bound = village.Bound;
            if (bound == null)
                return;

            // Player Settlement writes the owner into generated settlement XML. If an existing
            // generated fortification reaches the raid transition without that owner resolved,
            // restore it immediately before Bannerlord dispatches raid-state callbacks.
            if (bound.IsFortification && bound.OwnerClan == null &&
                IsPlayerSettlement(bound) && bound.Town != null && Clan.PlayerClan != null)
            {
                bound.Town.OwnerClan = Clan.PlayerClan;
            }

            // Castle-bound villages require a town trade bound for native village market and raid
            // listeners. Assign it only for the village that is entering the raid transition.
            if (bound.IsCastle && village.TradeBound == null && Campaign.Current != null)
            {
                Settlement tradeBound = Campaign.Current.Models.VillageTradeModel
                    .GetTradeBoundToAssignForVillage(village);
                if (tradeBound != null && tradeBound.IsTown)
                {
                    MethodInfo tradeBoundSetter = typeof(Village)
                        .GetProperty("TradeBound", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetSetMethod(true);
                    tradeBoundSetter?.Invoke(village, new object[] { tradeBound });
                }
            }
        }

        private static bool IsPlayerSettlementVillage(Settlement settlement)
        {
            return settlement != null && settlement.IsVillage && settlement.Village != null &&
                   IsPlayerSettlement(settlement);
        }

        private static bool IsPlayerSettlement(Settlement settlement)
        {
            return settlement != null && !String.IsNullOrEmpty(settlement.StringId) &&
                   settlement.StringId.StartsWith("player_settlement_", StringComparison.OrdinalIgnoreCase);
        }
    }
}
