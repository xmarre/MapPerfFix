using System;
using System.IO;

using HarmonyLib;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace BannerlordPlayerSettlement.Patches
{
    /// <summary>
    /// The automatic campaign reload can expose CampaignEvents.Tick before Bannerlord has
    /// restored the main-party and army reference graph. Vanilla tick listeners assume those
    /// globals are complete and throw from unrelated behaviors (food, army wait, etc.).
    /// Block the entire TickEvent dispatch until the graph is coherent. If an attachment remains
    /// provably stale for a sustained period, repair it through Bannerlord's public setters.
    /// </summary>
    [HarmonyPatch(typeof(CampaignEventDispatcher), nameof(CampaignEventDispatcher.Tick))]
    internal static class CampaignEventDispatcherPostLoadSafetyPatch
    {
        private const int RepairAfterBlockedTicks = 120;
        private static int _blockedTicks;
        private static bool _reportedEpisode;

        [HarmonyPrefix]
        private static bool Prefix()
        {
            try
            {
                if (Campaign.Current == null || Game.Current == null)
                {
                    ResetEpisode();
                    return true;
                }

                MobileParty? mainParty = MobileParty.MainParty;
                Hero? mainHero = Hero.MainHero;
                if (mainParty == null || mainHero == null || mainParty.Party == null)
                {
                    return Block("Waiting for main party and hero initialization");
                }

                PartyBase? partyBase;
                try
                {
                    partyBase = PartyBase.MainParty;
                }
                catch (NullReferenceException)
                {
                    return Block("Waiting for PartyBase.MainParty initialization");
                }

                if (partyBase == null || !ReferenceEquals(partyBase, mainParty.Party))
                {
                    return Block("Waiting for main-party PartyBase linkage");
                }

                MobileParty? heroParty = mainHero.PartyBelongedTo;
                if (heroParty == null || !ReferenceEquals(heroParty, mainParty))
                {
                    return Block("Waiting for hero/main-party linkage");
                }

                MobileParty? attachedTo = mainParty.AttachedTo;
                Army? mainArmy = mainParty.Army;
                Army? heroArmy = heroParty.Army;

                if (attachedTo != null)
                {
                    Army? attachedArmy = attachedTo.Army;
                    bool coherent = attachedArmy != null &&
                                    mainArmy != null &&
                                    heroArmy != null &&
                                    ReferenceEquals(attachedArmy, mainArmy) &&
                                    ReferenceEquals(heroArmy, mainArmy) &&
                                    mainArmy.LeaderParty != null;

                    if (!coherent)
                    {
                        if (++_blockedTicks < RepairAfterBlockedTicks)
                        {
                            LogOnce("Deferring campaign TickEvent while attached-army references finish loading");
                            return false;
                        }

                        Log("Repairing stale attached-party/army state after reload");
                        mainParty.AttachedTo = null;

                        if (mainParty.Army != null &&
                            (mainParty.Army.LeaderParty == null ||
                             attachedArmy == null ||
                             !ReferenceEquals(mainParty.Army, attachedArmy)))
                        {
                            mainParty.Army = null;
                        }

                        _blockedTicks = 0;
                        return false;
                    }
                }
                else if (mainArmy != null)
                {
                    bool coherent = mainArmy.LeaderParty != null && ReferenceEquals(heroArmy, mainArmy);
                    if (!coherent)
                    {
                        if (++_blockedTicks < RepairAfterBlockedTicks)
                        {
                            LogOnce("Deferring campaign TickEvent while main-army references finish loading");
                            return false;
                        }

                        Log("Clearing orphaned main-party army state after reload");
                        mainParty.Army = null;
                        _blockedTicks = 0;
                        return false;
                    }
                }

                ResetEpisode();
                return true;
            }
            catch (Exception e)
            {
                LogOnce("Suppressed incomplete post-load campaign tick: " + e);
                _blockedTicks++;
                return false;
            }
        }

        private static bool Block(string message)
        {
            _blockedTicks++;
            LogOnce(message);
            return false;
        }

        private static void ResetEpisode()
        {
            _blockedTicks = 0;
            _reportedEpisode = false;
        }

        private static void LogOnce(string message)
        {
            if (_reportedEpisode)
            {
                return;
            }

            _reportedEpisode = true;
            Log(message);
        }

        private static void Log(string message)
        {
            try
            {
                string userDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Mount and Blade II Bannerlord");
                string directory = Path.Combine(userDir, "Configs", "BannerlordPlayerSettlement");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "post_load_tick_guard.log"),
                    DateTime.UtcNow.ToString("O") + " | " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
