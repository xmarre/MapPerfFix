using System;
using System.IO;

using HarmonyLib;

using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;

namespace BannerlordPlayerSettlement.Patches
{
    /// <summary>
    /// Bannerlord 1.3.15 assumes that, while the army-wait menu is active, a non-null
    /// MainParty.AttachedTo already has a fully restored Army chain. The automatic
    /// post-placement reload can expose that chain over separate load phases. The vanilla
    /// method dereferences it without null or consistency checks.
    ///
    /// This prefix is deliberately non-destructive: it only suppresses the vanilla tick
    /// while the exact reference chain it needs is incomplete. It does not detach the
    /// player or make the player leave an army.
    /// </summary>
    [HarmonyPatch(typeof(PlayerArmyWaitBehavior), "OnTick")]
    internal static class PlayerArmyWaitBehaviorOnTickSafetyPatch
    {
        private static string? _lastEpisodeSignature;

        [HarmonyPrefix]
        private static bool Prefix()
        {
            try
            {
                Campaign? campaign = Campaign.Current;
                MobileParty? mainParty = MobileParty.MainParty;
                if (campaign == null || mainParty == null)
                {
                    return false;
                }

                MobileParty? attachedTo = mainParty.AttachedTo;
                if (attachedTo == null)
                {
                    _lastEpisodeSignature = null;
                    return true;
                }

                // The original method only dereferences AttachedTo.Army when this menu is active.
                // Do not interfere with unrelated campaign ticks.
                string? menuId = campaign.CurrentMenuContext?.GameMenu?.StringId;
                if (!string.Equals(menuId, "army_wait", StringComparison.Ordinal))
                {
                    _lastEpisodeSignature = null;
                    return true;
                }

                Army? attachedArmy = attachedTo.Army;
                Army? mainArmy = mainParty.Army;
                MobileParty? heroParty = Hero.MainHero?.PartyBelongedTo;
                Army? heroArmy = heroParty?.Army;

                bool completeAndConsistent = attachedArmy != null &&
                                             mainArmy != null &&
                                             heroParty != null &&
                                             heroArmy != null &&
                                             attachedArmy.LeaderParty != null &&
                                             mainArmy.LeaderParty != null &&
                                             heroArmy.LeaderParty != null &&
                                             ReferenceEquals(attachedArmy, mainArmy) &&
                                             ReferenceEquals(mainArmy, heroArmy);

                if (!completeAndConsistent)
                {
                    string signature =
                        $"attachedArmy={State(attachedArmy)}, mainArmy={State(mainArmy)}, " +
                        $"heroParty={(heroParty == null ? "null" : "set")}, heroArmy={State(heroArmy)}, " +
                        $"sameAttachedMain={ReferenceEquals(attachedArmy, mainArmy)}, " +
                        $"sameMainHero={ReferenceEquals(mainArmy, heroArmy)}";
                    LogEpisode(signature);
                    return false;
                }

                _lastEpisodeSignature = null;
                return true;
            }
            catch (Exception e)
            {
                LogEpisode("suppressed exception: " + e);
                return false;
            }
        }

        private static string State(Army? army)
        {
            if (army == null)
            {
                return "null";
            }

            return army.LeaderParty == null ? "set/leader-null" : "set/leader-set";
        }

        private static void LogEpisode(string signature)
        {
            if (string.Equals(_lastEpisodeSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastEpisodeSignature = signature;
            try
            {
                string userDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Mount and Blade II Bannerlord");
                string directory = Path.Combine(userDir, "Configs", "BannerlordPlayerSettlement");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "army_attachment_repair.log"),
                    DateTime.UtcNow.ToString("O") +
                    " | Deferred unsafe PlayerArmyWaitBehavior tick: " +
                    signature + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
