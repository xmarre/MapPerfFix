using System;
using System.IO;

using HarmonyLib;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordPlayerSettlement.Patches
{
    /// <summary>
    /// Bannerlord 1.3.15 assumes that a non-null MainParty.AttachedTo always has a valid Army,
    /// and that the player's party has already restored the same Army. A save/reload can restore
    /// these references over separate load phases. The vanilla OnTick dereferences the incomplete
    /// chain without null checks.
    /// </summary>
    [HarmonyPatch(typeof(PlayerArmyWaitBehavior), "OnTick")]
    internal static class PlayerArmyWaitBehaviorOnTickSafetyPatch
    {
        private static bool _reportedCurrentEpisode;

        [HarmonyPrefix]
        private static bool Prefix()
        {
            try
            {
                MobileParty? mainParty = MobileParty.MainParty;
                if (mainParty == null)
                {
                    return false;
                }

                MobileParty? attachedTo = mainParty.AttachedTo;
                if (attachedTo == null)
                {
                    _reportedCurrentEpisode = false;
                    return true;
                }

                Army? attachedArmy = attachedTo.Army;
                Army? mainArmy = mainParty.Army;
                MobileParty? heroParty = Hero.MainHero?.PartyBelongedTo;
                Army? heroArmy = heroParty?.Army;

                bool staleAttachment = attachedArmy == null ||
                                       mainArmy == null ||
                                       !ReferenceEquals(attachedArmy, mainArmy);

                if (staleAttachment)
                {
                    LogOnce($"Repairing stale army attachment: attachedArmy={(attachedArmy == null ? "null" : "set")}, mainArmy={(mainArmy == null ? "null" : "set")}");

                    // Use Bannerlord's public setter so it removes the party from AttachedParties,
                    // clears associated event/siege state and resets movement normally.
                    mainParty.AttachedTo = null;

                    // An orphaned or mismatched Army must also be left through its public setter.
                    if (mainParty.Army != null &&
                        (attachedArmy == null ||
                         !ReferenceEquals(mainParty.Army, attachedArmy) ||
                         mainParty.Army.LeaderParty == null))
                    {
                        mainParty.Army = null;
                    }

                    return false;
                }

                // The core attachment agrees, but some load-time references may still be filled
                // on a later tick. Skip the unsafe vanilla method without altering valid state.
                if (attachedArmy.LeaderParty == null ||
                    heroParty == null ||
                    heroArmy == null ||
                    heroArmy.LeaderParty == null)
                {
                    LogOnce("Deferring PlayerArmyWaitBehavior until army references finish loading");
                    return false;
                }

                _reportedCurrentEpisode = false;
                return true;
            }
            catch (Exception e)
            {
                LogOnce("Suppressed PlayerArmyWaitBehavior state exception: " + e);
                return false;
            }
        }

        private static void LogOnce(string message)
        {
            if (_reportedCurrentEpisode)
            {
                return;
            }

            _reportedCurrentEpisode = true;
            try
            {
                string userDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Mount and Blade II Bannerlord");
                string directory = Path.Combine(userDir, "Configs", "BannerlordPlayerSettlement");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "army_attachment_repair.log"),
                    DateTime.UtcNow.ToString("O") + " | " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
