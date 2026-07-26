using System;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace PlayerSettlementVillageRaidFix
{
    public sealed class SubModule : MBSubModuleBase
    {
        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (!(game.GameType is Campaign))
                return;

            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        private static void OnSessionLaunched(CampaignGameStarter starter)
        {
            PlayerSettlementVillageInvariantRepair.Repair();
        }
    }

    internal static class PlayerSettlementVillageInvariantRepair
    {
        private const string PlayerSettlementPrefix = "player_settlement_";

        private static readonly MethodInfo SettlementComponentOwnerSetter =
            typeof(SettlementComponent)
                .GetProperty("Owner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetSetMethod(true);

        private static readonly MethodInfo VillageTradeBoundSetter =
            typeof(Village)
                .GetProperty("TradeBound", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetSetMethod(true);

        internal static void Repair()
        {
            Campaign campaign = Campaign.Current;
            if (campaign == null)
                return;

            int checkedVillages = 0;
            int repairedReferences = 0;
            int unresolvedVillages = 0;

            foreach (Settlement settlement in Settlement.All)
            {
                if (!IsPlayerSettlementVillage(settlement))
                    continue;

                checkedVillages++;
                try
                {
                    Village village = settlement.Village;
                    bool unresolved = false;

                    if (village.Owner == null)
                    {
                        if (settlement.Party != null && SettlementComponentOwnerSetter != null)
                        {
                            SettlementComponentOwnerSetter.Invoke(village, new object[] { settlement.Party });
                            repairedReferences++;
                        }
                        else
                        {
                            unresolved = true;
                        }
                    }

                    Settlement bound = village.Bound;
                    if (bound == null)
                    {
                        unresolved = true;
                    }
                    else
                    {
                        if (bound.IsFortification && bound.OwnerClan == null &&
                            IsPlayerSettlement(bound) && bound.Town != null && Clan.PlayerClan != null)
                        {
                            bound.Town.OwnerClan = Clan.PlayerClan;
                            repairedReferences++;
                        }

                        if (bound.OwnerClan == null || bound.MapFaction == null)
                            unresolved = true;

                        if (bound.IsCastle && village.TradeBound == null)
                        {
                            Settlement tradeBound = campaign.Models.VillageTradeModel
                                .GetTradeBoundToAssignForVillage(village);

                            if (tradeBound != null && tradeBound.IsTown && VillageTradeBoundSetter != null)
                            {
                                VillageTradeBoundSetter.Invoke(village, new object[] { tradeBound });
                                repairedReferences++;
                            }
                            else
                            {
                                unresolved = true;
                            }
                        }
                    }

                    if (village.Owner == null || village.Settlement == null)
                        unresolved = true;

                    if (unresolved)
                        unresolvedVillages++;
                }
                catch (Exception ex)
                {
                    unresolvedVillages++;
                    Debug.Print("[PlayerSettlementVillageRaidFix] Failed to repair " +
                                settlement.StringId + ": " + ex);
                }
            }

            Debug.Print("[PlayerSettlementVillageRaidFix] Checked " + checkedVillages +
                        " player village(s); restored " + repairedReferences +
                        " missing native reference(s); unresolved " + unresolvedVillages + ".");
        }

        private static bool IsPlayerSettlementVillage(Settlement settlement)
        {
            return settlement != null && settlement.IsVillage && settlement.Village != null &&
                   IsPlayerSettlement(settlement);
        }

        private static bool IsPlayerSettlement(Settlement settlement)
        {
            return settlement != null && !String.IsNullOrEmpty(settlement.StringId) &&
                   settlement.StringId.StartsWith(PlayerSettlementPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
