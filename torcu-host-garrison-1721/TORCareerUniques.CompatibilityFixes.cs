using System;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

[assembly: AssemblyVersion("1.7.21.0")]
[assembly: AssemblyFileVersion("1.7.21.0")]

namespace TORCareerUniques.CompatibilityFixes
{
    public sealed class CompatibilityFixSubModule : MBSubModuleBase
    {
        private const string HarmonyId = "torcareeruniques.compatibilityfixes.1.7.21";
        private static bool _installed;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            if (_installed)
                return;

            var harmony = new Harmony(HarmonyId);
            InstallDedicatedClanFinancialGuard(harmony);
            InstallOssuaryGraspHumanRaceGuard(harmony);
            InstallStaticGarrisonAvoidanceGuard(harmony);
            _installed = true;
        }

        private static void InstallDedicatedClanFinancialGuard(Harmony harmony)
        {
            Type behaviorType = AccessTools.TypeByName(
                "TaleWorlds.CampaignSystem.CampaignBehaviors.ClanVariablesCampaignBehavior");
            if (behaviorType == null)
                throw new TypeLoadException("ClanVariablesCampaignBehavior was not found.");

            MethodInfo dailyTickClan = AccessTools.Method(
                behaviorType,
                "DailyTickClan",
                new[] { typeof(Clan) });
            if (dailyTickClan == null)
                throw new MissingMethodException(behaviorType.FullName, "DailyTickClan(Clan)");

            MethodInfo prefix = AccessTools.Method(
                typeof(CompatibilityFixSubModule),
                nameof(BeforeClanVariablesDailyTickClan));
            harmony.Patch(dailyTickClan, prefix: new HarmonyMethod(prefix));
        }

        private static void InstallOssuaryGraspHumanRaceGuard(Harmony harmony)
        {
            Type managerType = AccessTools.TypeByName(
                "TOR_Core.Items.ExtendedItemObjectManager");
            if (managerType == null)
                throw new TypeLoadException("TOR ExtendedItemObjectManager was not found.");

            MethodInfo raceCheck = AccessTools.Method(
                managerType,
                "CanCharacterUseItemBasedOnRace",
                new[] { typeof(ItemObject), typeof(BasicCharacterObject) });
            if (raceCheck == null)
                throw new MissingMethodException(
                    managerType.FullName,
                    "CanCharacterUseItemBasedOnRace(ItemObject, BasicCharacterObject)");

            MethodInfo prefix = AccessTools.Method(
                typeof(CompatibilityFixSubModule),
                nameof(BeforeCanCharacterUseItemBasedOnRace));
            harmony.Patch(raceCheck, prefix: new HarmonyMethod(prefix));
        }

        private static void InstallStaticGarrisonAvoidanceGuard(Harmony harmony)
        {
            Type modelType = AccessTools.TypeByName(
                "TaleWorlds.CampaignSystem.GameComponents.DefaultMobilePartyAIModel");
            if (modelType == null)
                throw new TypeLoadException("DefaultMobilePartyAIModel was not found.");

            MethodInfo shouldAvoid = AccessTools.Method(
                modelType,
                "ShouldConsiderAvoiding",
                new[] { typeof(MobileParty), typeof(MobileParty) });
            if (shouldAvoid == null)
                throw new MissingMethodException(
                    modelType.FullName,
                    "ShouldConsiderAvoiding(MobileParty, MobileParty)");

            MethodInfo prefix = AccessTools.Method(
                typeof(CompatibilityFixSubModule),
                nameof(BeforeShouldConsiderAvoiding));
            harmony.Patch(shouldAvoid, prefix: new HarmonyMethod(prefix));
        }

        public static bool BeforeClanVariablesDailyTickClan(Clan __0)
        {
            return !IsDedicatedEncounterClan(__0);
        }

        public static bool BeforeCanCharacterUseItemBasedOnRace(
            ItemObject __0,
            BasicCharacterObject __1,
            ref bool __result)
        {
            if (__0 == null || __1 == null || __1.Race != 0)
                return true;

            string name = __0.Name == null ? String.Empty : __0.Name.ToString();
            if (!name.EndsWith("Ossuary Grasp", StringComparison.Ordinal))
                return true;

            __result = true;
            return false;
        }

        public static bool BeforeShouldConsiderAvoiding(
            MobileParty party,
            MobileParty targetParty,
            ref bool __result)
        {
            if (!IsEncounterHost(party) || targetParty == null ||
                !targetParty.IsGarrison || targetParty.CurrentSettlement == null)
                return true;

            // Bannerlord's native AI treats every hostile garrison as an avoidance
            // threat. A garrison still inside its settlement cannot chase a roaming
            // host, so fleeing from it causes irrational long-distance detours.
            // Ignore only static in-settlement garrisons; genuinely mobile hostile
            // parties continue through the native avoidance model unchanged.
            __result = false;
            return false;
        }

        private static bool IsEncounterHost(MobileParty party)
        {
            return party != null &&
                !String.IsNullOrEmpty(party.StringId) &&
                party.StringId.StartsWith("torcu_enc_", StringComparison.Ordinal);
        }

        private static bool IsDedicatedEncounterClan(Clan clan)
        {
            return clan != null &&
                !String.IsNullOrEmpty(clan.StringId) &&
                clan.StringId.StartsWith("torcu_faction_", StringComparison.Ordinal);
        }
    }
}
