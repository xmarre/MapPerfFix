using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

[assembly: AssemblyVersion("1.7.17.0")]
[assembly: AssemblyFileVersion("1.7.17.0")]

namespace TORCareerUniques.TavernRumors
{
    public sealed class TavernRumorSubModule : MBSubModuleBase
    {
        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            CampaignGameStarter starter = gameStarterObject as CampaignGameStarter;
            if (starter != null)
                starter.AddBehavior(new TavernRumorBehavior());
        }
    }

    internal sealed class EncounterRumorScript
    {
        public string CareerId;
        public string ChoiceText;
        public string DetailText;
        public string AcceptText;
        public string SuccessText;
        public string DeclineText;
        public string LostText;
    }

    internal sealed class RuntimeDefinition
    {
        public object Raw;
        public string CareerId;
        public string MapName;
        public string Kind;
    }

    internal sealed class TavernRumorBehavior : CampaignBehaviorBase
    {
        private const float TavernRumorRadius = 180f;
        private const int TavernRumorNearestTownCoverage = 3;
        private const string SitePrefix = "torcu_site_";

        private string _lastRumorCareerId;
        private bool _lastRumorTrackSucceeded;
        private string _lastRumorTrackTargetName;

        private static readonly EncounterRumorScript[] TavernRumorScripts = BuildTavernRumorScripts();
        private static readonly Dictionary<string, EncounterRumorScript> TavernRumorScriptsByCareer = BuildTavernRumorScriptMap();

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
        }

        public override void SyncData(IDataStore dataStore) { }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            AddTavernRumorDialogs(starter);
        }

        private static Dictionary<string, EncounterRumorScript> BuildTavernRumorScriptMap()
        {
            Dictionary<string, EncounterRumorScript> result = new Dictionary<string, EncounterRumorScript>(StringComparer.Ordinal);
            for (int i = 0; i < TavernRumorScripts.Length; i++)
                result[TavernRumorScripts[i].CareerId] = TavernRumorScripts[i];
            return result;
        }

        private static EncounterRumorScript[] BuildTavernRumorScripts()
        {
            return new[]
            {
                Rumor("GrailDamsel",
                    "What is this I hear about a desecrated chapel?",
                    "A charcoal burner came through pale as milk. He found a ruined Grail chapel where no birds sing, and saw {TORCU_RUMOR_LEADER} keeping watch over what the dead left behind. Folk call it the Blighted Grail Chapel. Those who go searching its reliquary seldom come back clean in body or soul.",
                    "Show me where that chapel lies.",
                    "Then follow the old pilgrim road toward {TORCU_RUMOR_TRACK_TARGET}. I have marked the place the charcoal burner described; from there, the blighted chapel should be found.",
                    "A cursed chapel can keep its secrets for now.",
                    "That tale has gone cold. The chapel or its guardian can no longer be placed with any honesty, so I will not mark a false trail."),

                Rumor("GrailKnight",
                    "Have travelers seen black-armoured knights on the roads?",
                    "They have. A black procession rides beneath dead pennants, with {TORCU_RUMOR_LEADER} at its head. They circle the old Bretonnian roads like a funeral that never reaches the grave, carrying trophies taken from fallen Grail pilgrims. Men here have named them the Black Grail Procession.",
                    "Mark the road where that procession was last seen.",
                    "Their trail was freshest near {TORCU_RUMOR_TRACK_TARGET}. I have marked it. Expect mounted killers, not roadside brigands.",
                    "I will let that procession pass for now.",
                    "The riders have slipped the tale. No trustworthy road can be marked for them now."),

                Rumor("MinorVampire",
                    "Do you know anything about an old sealed sepulchre?",
                    "Grave-robbers whisper of the Sepulchre of the Red Duke. They say {TORCU_RUMOR_LEADER} is tied to the place, and that lesser blood-drinkers left their effects sealed beneath stone there. One robber returned with silver in his purse and no blood in his face. The others did not return at all.",
                    "Put the sepulchre on my map.",
                    "Look toward {TORCU_RUMOR_TRACK_TARGET}. I have marked the nearest sure landmark; the sepulchre lies in that country.",
                    "I have no wish to open a vampire's tomb today.",
                    "No reliable path to the sepulchre remains. I would rather admit ignorance than send you to the wrong grave."),

                Rumor("WarriorPriest",
                    "Who is conducting that hard-line Sigmarite purge?",
                    "That would be {TORCU_RUMOR_LEADER}. The company calls its work the Purple Hand Purge. They move from road to road with temple relics, seized charms and the possessions of men they judged heretics. Their zeal has made enemies of cultists, mutants and more ordinary folk alike.",
                    "Tell me where the purge is operating now.",
                    "Their latest reports point to {TORCU_RUMOR_TRACK_TARGET}. I have marked the trail. Do not expect {TORCU_RUMOR_LEADER} to mistake armed strangers for innocent pilgrims.",
                    "Sigmar's quarrels can wait.",
                    "The purge has moved beyond every report I trust. I cannot mark its present trail."),

                Rumor("BloodKnight",
                    "Any word of Blood Dragons riding challenges through the borderlands?",
                    "Aye. The Crimson Errantry. {TORCU_RUMOR_LEADER} rides with vampires and dead knights who treat every armed company as an invitation to prove themselves. They leave broken lances, emptied bodies and very few witnesses behind them.",
                    "Mark their latest trail.",
                    "The freshest word puts the Crimson Errantry near {TORCU_RUMOR_TRACK_TARGET}. I have marked it. If you seek them, they are unlikely to refuse the challenge.",
                    "I am not chasing Blood Dragons today.",
                    "Their trail has vanished between one telling and the next. There is nothing honest for me to mark."),

                Rumor("Mercenary",
                    "What do you know of the Border Princes' Black Company?",
                    "Merchants curse them and poorer mercenaries envy them. {TORCU_RUMOR_LEADER} keeps the Black Company moving between trade roads with a pay chest full of trophies, unpaid contracts and whatever else their employers failed to reclaim. They are disciplined enough to be dangerous and faithless enough to be profitable.",
                    "Show me where the Black Company is camped.",
                    "Their last dependable trail runs by {TORCU_RUMOR_TRACK_TARGET}. It is marked. Watch for scouts before you ever see the main company.",
                    "Another mercenary company is no concern of mine.",
                    "The Black Company has broken camp and the newer reports contradict one another. I will not invent a location."),

                Rumor("WitchHunter",
                    "People keep mentioning an Ashen Tribunal. What is it?",
                    "A burned coven turned interrogation ground. {TORCU_RUMOR_LEADER} holds it with Witch Hunter retainers and stores confiscated charms, execution tools and evidence there. The locals call it the Ashen Tribunal, though most lower their voices before saying the name.",
                    "Mark the Ashen Tribunal for me.",
                    "Travel toward {TORCU_RUMOR_TRACK_TARGET}. I have marked the nearest certain point. Announce yourself carefully unless you enjoy explaining why you came armed to a Witch Hunter's door.",
                    "I will stay clear of the Tribunal for now.",
                    "The Tribunal cannot presently be located with confidence. Better no mark than one that leads you into an innocent hamlet."),

                Rumor("Necromancer",
                    "Have the dead really been stirring around an old barrow?",
                    "They have, if half the frightened stories are true. The Barrow of the Restless Host still has an intact burial chamber beneath it, and {TORCU_RUMOR_LEADER} is bound up in the latest tales. Grave goods remain below, along with things that object strongly to grave-robbers.",
                    "Show me the barrow's location.",
                    "I have marked the country around {TORCU_RUMOR_TRACK_TARGET}. The barrow lies along that route. Take fire, steel and little faith in the dead staying down.",
                    "Let the restless keep their barrow.",
                    "The barrow's trail is no longer reliable enough to mark. Something has changed since the last report."),

                Rumor("BlackGrailKnight",
                    "Who guards the black reliquary train I've heard about?",
                    "The name passed from mouth to mouth is {TORCU_RUMOR_LEADER}. The Black Grail Reliquary Guard hunts every rumour of the Black Grail with damned chivalry and undead retainers around its wagons. Even men who know nothing of the relic give that column a very wide road.",
                    "Mark the reliquary guard's route.",
                    "The train was last placed near {TORCU_RUMOR_TRACK_TARGET}. I have marked that lead. A column like that leaves tracks, but it also leaves bait for pursuers.",
                    "I will not follow a damned reliquary train yet.",
                    "The reliquary train has moved beyond the reports I trust. I cannot give you a true mark."),

                Rumor("Necrarch",
                    "Is there truth to the stories of a hidden ossuary laboratory?",
                    "Too much truth. The Necrarch Ossuary is spoken of as a nest of warped experiments, old grimoires and dead apprentices. {TORCU_RUMOR_LEADER} is the name attached to the place now. Shepherds avoid the ground even when it means losing half a day's grazing.",
                    "Put the ossuary on my map.",
                    "The surest approach begins near {TORCU_RUMOR_TRACK_TARGET}. I have marked it. If the smell turns sweet before it turns foul, the old men say you are already too close.",
                    "Some laboratories are best left undiscovered.",
                    "The ossuary cannot be placed from the reports now in circulation. I have no honest mark to give."),

                Rumor("WarriorPriestUlric",
                    "Who are the hunters wearing the White Wolf?",
                    "Ulricans on a hard hunt. {TORCU_RUMOR_LEADER} leads what people call the White Wolf Hunt, taking the winter roads after beasts, raiders and things they consider unworthy of the north. Shrine relics and spoils from dead enemies travel with them.",
                    "Mark the White Wolf Hunt's trail.",
                    "Their last reliable passage was near {TORCU_RUMOR_TRACK_TARGET}. I have marked it. Cold roads hide tracks well, so do not expect the trail to stay fresh forever.",
                    "The White Wolf can hunt without me.",
                    "The hunt has outrun the reports. There is no current trail I can swear to."),

                Rumor("ImperialMagister",
                    "What happened at the ruined Collegiate observatory?",
                    "Officially, very little. Unofficially, enough that {TORCU_RUMOR_LEADER} keeps an isolated Collegiate retinue around the ruins. Sealed instruments, charts and magical apparatus remain there, which is exactly why cultists and rogue sorcerers keep testing the perimeter.",
                    "Show me the observatory.",
                    "The safest known approach begins around {TORCU_RUMOR_TRACK_TARGET}. I have marked it. Whether the magisters consider you safer than the things outside is another question.",
                    "I will leave the Colleges to their instruments.",
                    "The observatory's current approach is no longer certain enough to mark."),

                Rumor("Waywatcher",
                    "Have Asrai hunters been seen beyond the deep forest?",
                    "Yes. The Beast-Hunters of Athel Loren range farther than most men realize. {TORCU_RUMOR_LEADER} leads scouts hunting Beastmen and other trespassers, carrying heirlooms of fallen kin beneath green cloaks and very little patience for strangers.",
                    "Mark where the Beast-Hunters were last seen.",
                    "The best report places their trail near {TORCU_RUMOR_TRACK_TARGET}. I have marked it. Do not assume you are unseen merely because the road looks empty.",
                    "I will not follow Asrai into the woods today.",
                    "Their trail has disappeared into country where tavern gossip stops being useful. I cannot mark them now."),

                Rumor("Spellsinger",
                    "What is the story behind the silent grove?",
                    "A grove where even insects seem reluctant to make noise. Travelers speak of old waystones, lingering magic and {TORCU_RUMOR_LEADER} guarding what the Asrai left there. They call it the Silent Grove of the Lost Song.",
                    "Show me the silent grove.",
                    "I have marked the nearest trustworthy approach at {TORCU_RUMOR_TRACK_TARGET}. Beyond that, listen for the place where the forest goes unnaturally still.",
                    "I have no business in an enchanted grove yet.",
                    "The grove cannot be fixed to a trustworthy approach at present. I will not mark a guess."),

                Rumor("Warden",
                    "Who is guarding the broken waystone?",
                    "The Broken Waystone Watch. {TORCU_RUMOR_LEADER} is the name tied to it. The old stone was damaged badly enough that the Asrai now treat the surrounding ground as something between a shrine, a wound and a military post.",
                    "Mark the broken waystone for me.",
                    "Travel toward {TORCU_RUMOR_TRACK_TARGET}. I have marked the nearest reliable point. Beyond it, do not touch anything carved with roots unless you know what the carving means.",
                    "The waystone can wait.",
                    "The watch's present location cannot be fixed with enough certainty to mark."),

                Rumor("GreyLord",
                    "Have you heard of a Grey Lord conclave nearby?",
                    "Only in careful whispers. {TORCU_RUMOR_LEADER} is associated with a gathering of elder mages and retainers who move between secluded places with old relics and stranger purposes. People call it the Grey Conclave because few know enough to call it anything more precise.",
                    "Mark the conclave's latest location.",
                    "The most reliable lead points toward {TORCU_RUMOR_TRACK_TARGET}. I have marked it. A conclave that wishes to stay hidden is rarely pleased by visitors who found it through tavern talk.",
                    "I will leave elder mages to their secrets.",
                    "The conclave has shifted or gone to ground. No current mark would be trustworthy."),

                Rumor("KnightOldWorld",
                    "Who are the knights people call the Black Road Brotherhood?",
                    "A hard-riding brotherhood under {TORCU_RUMOR_LEADER}, travelling old roads with captured standards and relics won in private wars. They are neither common bandits nor a lord's proper host, which makes every village nervous when their outriders appear.",
                    "Show me where the Brotherhood is riding.",
                    "Their latest dependable sightings cluster near {TORCU_RUMOR_TRACK_TARGET}. I have marked the lead. Expect roadblocks before banners.",
                    "The Black Road can wait.",
                    "The Brotherhood has shifted its road. The old mark would be a lie now."),

                Rumor("Ironbreaker",
                    "What drove everyone away from the goblin-delved underhold?",
                    "Greenskins broke into an old Dawi hold from below and made a ruin of passages that were never meant for them. {TORCU_RUMOR_LEADER} is bound to the effort around the underhold now. The place is cramped, trapped and full of grudges older than anyone drinking in this room.",
                    "Show me the underhold.",
                    "The nearest sound approach is by {TORCU_RUMOR_TRACK_TARGET}. I have marked it. In an underhold, the shortest tunnel is rarely the safest one.",
                    "I am in no hurry to crawl through goblin tunnels.",
                    "The underhold's usable approach cannot presently be confirmed. I will not mark a dead tunnel as a road."),

                Rumor("Slayer",
                    "Have any Slayer bands passed through hunting something large?",
                    "A band known as the Troll King's Hunters has. {TORCU_RUMOR_LEADER} is with them, and they are pursuing the sort of quarry that makes ordinary monster-hunters reconsider their profession. Every telling makes the trolls larger; the missing livestock, at least, are real.",
                    "Mark the Slayers' latest trail.",
                    "They were last placed near {TORCU_RUMOR_TRACK_TARGET}. I have marked it. Follow broken trees and arguments about who gets the biggest monster.",
                    "I will leave the doom-seekers to their quarry.",
                    "The Slayers have moved on and no fresh witness agrees where. I cannot mark them now."),

                Rumor("Runelord",
                    "Is there really a desecrated rune vault nearby?",
                    "Dawi traders say there is, though they dislike outsiders asking. Old runes were profaned and relics disturbed; {TORCU_RUMOR_LEADER} is tied to the vault's present guardianship. Even a dwarf willing to sell the tale will not joke about what lies inside.",
                    "Put the rune vault on my map.",
                    "The best approach is through the country around {TORCU_RUMOR_TRACK_TARGET}. I have marked it. Do not mistake a sealed Dawi door for an invitation merely because you found it.",
                    "The Dawi can settle that grudge without me.",
                    "No current report fixes the rune vault well enough for a mark. The old directions are no longer dependable."),

                Rumor("OrcBoss",
                    "Which greenskin boss is gathering that rival Waaagh!?",
                    "The warband is spoken of as Grubnash's Rival Waaagh!, though {TORCU_RUMOR_LEADER} is the name attached to the force now. It grows by boasting, fighting and absorbing whichever mobs survive. Villages measure its distance by how many nights the horizon has been orange.",
                    "Show me where that Waaagh! is gathering.",
                    "The mob was last reliably seen near {TORCU_RUMOR_TRACK_TARGET}. I have marked it. You will probably hear them before you reach the mark.",
                    "I do not need a Waaagh! today.",
                    "The greenskins have surged somewhere beyond the latest reports. There is no useful mark I can give."),

                Rumor("OrcShaman",
                    "What is drawing greenskins to the Moon-Idol Hollow?",
                    "A crude moon-idol, bad magic and enough greenskins believing in both to make the belief dangerous. {TORCU_RUMOR_LEADER} is tied to the hollow now. On certain nights, locals swear the chanting carries farther than sound ought to travel.",
                    "Mark the Moon-Idol Hollow.",
                    "Head toward {TORCU_RUMOR_TRACK_TARGET}. I have marked the nearest reliable landmark. If the moon starts looking larger while the chanting gets quieter, turn around.",
                    "I will leave the moon-idol alone for now.",
                    "The hollow's present location cannot be confirmed from the reports I trust. I will not mark superstition as certainty.")
            };
        }

        private static EncounterRumorScript Rumor(string careerId,
            string choiceText, string detailText, string acceptText,
            string successText, string declineText, string lostText)
        {
            return new EncounterRumorScript
            {
                CareerId = careerId,
                ChoiceText = choiceText,
                DetailText = detailText,
                AcceptText = acceptText,
                SuccessText = successText,
                DeclineText = declineText,
                LostText = lostText
            };
        }

        private void AddTavernRumorDialogs(CampaignGameStarter starter)
        {
            if (starter == null)
                return;

            starter.AddPlayerLine("torcu_rumor_ask", "tavernkeeper_talk",
                "torcu_rumor_intro",
                "Have you heard anything useful about dangerous strangers or forgotten places nearby?",
                HasAnyTavernRumor, null, 105);
            starter.AddDialogLine("torcu_rumor_intro", "torcu_rumor_intro",
                "torcu_rumor_hub",
                "Aye. A few stories around here are worth hearing, if you intend to act on them. Which sort of trouble interests you?",
                null, null);

            for (int i = 0; i < TavernRumorScripts.Length; i++)
            {
                EncounterRumorScript script = TavernRumorScripts[i];
                string slug = Slug(script.CareerId);
                string detailState = "torcu_rumor_detail_" + slug;
                string decisionState = "torcu_rumor_decide_" + slug;
                string resultState = "torcu_rumor_result_" + slug;

                starter.AddPlayerLine("torcu_rumor_pick_" + slug,
                    "torcu_rumor_hub", detailState, script.ChoiceText,
                    delegate { return PrepareTavernRumor(script.CareerId); },
                    null, 100);
                starter.AddDialogLine("torcu_rumor_detail_npc_" + slug,
                    detailState, decisionState, script.DetailText,
                    delegate { return PrepareTavernRumor(script.CareerId); },
                    null);
                starter.AddPlayerLine("torcu_rumor_accept_" + slug,
                    decisionState, resultState, script.AcceptText,
                    delegate { return IsRumorEncounterCurrentlyValid(script.CareerId); },
                    delegate { AcceptTavernRumorLead(script.CareerId); }, 100);
                starter.AddPlayerLine("torcu_rumor_decline_" + slug,
                    decisionState, "torcu_rumor_hub", script.DeclineText,
                    null, null, 90);
                starter.AddDialogLine("torcu_rumor_success_" + slug,
                    resultState, "torcu_rumor_hub", script.SuccessText,
                    delegate { return PrepareRumorResult(script.CareerId, true); },
                    ClearRumorResult);
                starter.AddDialogLine("torcu_rumor_lost_" + slug,
                    resultState, "torcu_rumor_hub", script.LostText,
                    delegate { return PrepareRumorResult(script.CareerId, false); },
                    ClearRumorResult);
            }

            starter.AddPlayerLine("torcu_rumor_done", "torcu_rumor_hub",
                "tavernkeeper_talk", "That is enough rumor for now.",
                null, null, 10);
        }

        private bool HasAnyTavernRumor()
        {
            Settlement town = Settlement.CurrentSettlement;
            if (town == null || !town.IsTown)
                return false;
            List<RuntimeDefinition> definitions = GetDefinitions();
            for (int i = 0; i < definitions.Count; i++)
                if (IsRumorAvailableAtTown(definitions[i], town))
                    return true;
            return false;
        }

        private bool PrepareTavernRumor(string careerId)
        {
            RuntimeDefinition definition = GetDefinition(careerId);
            Settlement town = Settlement.CurrentSettlement;
            if (town == null || !town.IsTown || definition == null ||
                !TavernRumorScriptsByCareer.ContainsKey(careerId) ||
                !IsRumorAvailableAtTown(definition, town))
                return false;

            Hero leader = GetActiveEncounterHero(careerId);
            string leaderName = leader != null && leader.Name != null
                ? leader.Name.ToString() : GetFallbackLeaderName(careerId);
            MBTextManager.SetTextVariable("TORCU_RUMOR_LEADER", leaderName);
            return true;
        }

        private bool IsRumorAvailableAtTown(RuntimeDefinition definition, Settlement town)
        {
            if (definition == null || town == null || !town.IsTown ||
                !IsRumorEncounterCurrentlyValid(definition.CareerId))
                return false;

            CampaignVec2 targetPosition;
            if (!TryGetRumorTargetPosition(definition, out targetPosition))
                return false;

            float distanceSquared = town.GatePosition.DistanceSquared(targetPosition);
            if (distanceSquared <= TavernRumorRadius * TavernRumorRadius)
                return true;
            return IsAmongNearestRumorTowns(town, targetPosition, TavernRumorNearestTownCoverage);
        }

        private bool IsRumorEncounterCurrentlyValid(string careerId)
        {
            RuntimeDefinition definition = GetDefinition(careerId);
            object behavior = GetMainBehavior();
            if (definition == null || behavior == null)
                return false;

            if (!IsEncounterHeroAvailable(behavior, careerId))
                return false;

            IDictionary respawnDays = GetFieldValue(behavior, "_respawnAtDay") as IDictionary;
            if (respawnDays != null && respawnDays.Contains(careerId))
            {
                object value = respawnDays[careerId];
                double respawnDay = Convert.ToDouble(value);
                if (CampaignTime.Now.ToDays < respawnDay)
                    return false;
            }

            IList pendingRewards = GetFieldValue(behavior, "_pendingRewards") as IList;
            if (pendingRewards != null && pendingRewards.Contains(careerId))
                return false;

            if (String.Equals(definition.Kind, "RoamingHost", StringComparison.Ordinal))
                return FindActiveEncounter(behavior, careerId) != null;
            return FindGuardianSite(behavior, definition.Raw) != null;
        }

        private bool TryGetRumorTargetPosition(RuntimeDefinition definition, out CampaignVec2 position)
        {
            position = default(CampaignVec2);
            object behavior = GetMainBehavior();
            if (definition == null || behavior == null)
                return false;

            if (String.Equals(definition.Kind, "RoamingHost", StringComparison.Ordinal))
            {
                MobileParty party = FindActiveEncounter(behavior, definition.CareerId);
                if (party == null || !party.IsActive)
                    return false;
                position = party.Position;
                return true;
            }

            Settlement site = FindGuardianSite(behavior, definition.Raw);
            if (site == null)
                return false;
            position = site.GatePosition;
            return true;
        }

        private static bool IsAmongNearestRumorTowns(Settlement currentTown,
            CampaignVec2 targetPosition, int nearestTownCount)
        {
            if (currentTown == null || !currentTown.IsTown || nearestTownCount <= 0)
                return false;
            float currentDistance = targetPosition.DistanceSquared(currentTown.GatePosition);
            int closerTowns = 0;
            foreach (Settlement settlement in Settlement.All)
            {
                if (settlement == null || !settlement.IsTown ||
                    Object.ReferenceEquals(settlement, currentTown) ||
                    String.IsNullOrEmpty(settlement.StringId) ||
                    settlement.StringId.StartsWith(SitePrefix, StringComparison.Ordinal))
                    continue;
                if (targetPosition.DistanceSquared(settlement.GatePosition) < currentDistance)
                {
                    closerTowns++;
                    if (closerTowns >= nearestTownCount)
                        return false;
                }
            }
            return true;
        }

        private void AcceptTavernRumorLead(string careerId)
        {
            _lastRumorCareerId = careerId;
            _lastRumorTrackSucceeded = false;
            _lastRumorTrackTargetName = null;

            RuntimeDefinition definition = GetDefinition(careerId);
            object behavior = GetMainBehavior();
            if (definition == null || behavior == null ||
                !IsRumorEncounterCurrentlyValid(careerId))
                return;

            Settlement trackingSettlement = null;
            if (String.Equals(definition.Kind, "RoamingHost", StringComparison.Ordinal))
            {
                MobileParty party = FindActiveEncounter(behavior, careerId);
                if (party != null && party.IsActive)
                    trackingSettlement = FindNearestTrackableSettlement(behavior.GetType(), party.Position);
            }
            else
            {
                trackingSettlement = FindGuardianSite(behavior, definition.Raw);
            }

            if (trackingSettlement == null)
                return;

            _lastRumorTrackTargetName = trackingSettlement.Name == null
                ? trackingSettlement.StringId : trackingSettlement.Name.ToString();
            _lastRumorTrackSucceeded = TryTrackOnMap(behavior.GetType(), trackingSettlement);
        }

        private bool PrepareRumorResult(string careerId, bool success)
        {
            if (!String.Equals(_lastRumorCareerId, careerId, StringComparison.Ordinal) ||
                _lastRumorTrackSucceeded != success)
                return false;
            if (success)
                MBTextManager.SetTextVariable("TORCU_RUMOR_TRACK_TARGET",
                    String.IsNullOrEmpty(_lastRumorTrackTargetName)
                        ? "the last reliable landmark" : _lastRumorTrackTargetName);
            return true;
        }

        private void ClearRumorResult()
        {
            _lastRumorCareerId = null;
            _lastRumorTrackSucceeded = false;
            _lastRumorTrackTargetName = null;
        }

        private static object GetMainBehavior()
        {
            try
            {
                Type adminType = Type.GetType("TORCareerUniques.AdminBridge, TORCareerUniques", false);
                if (adminType == null)
                    return null;
                FieldInfo field = adminType.GetField("_behavior", BindingFlags.Static | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(null);
            }
            catch { return null; }
        }

        private static List<RuntimeDefinition> GetDefinitions()
        {
            List<RuntimeDefinition> result = new List<RuntimeDefinition>();
            try
            {
                Type catalogType = Type.GetType("TORCareerUniques.EncounterCatalog, TORCareerUniques", false);
                if (catalogType == null)
                    return result;
                FieldInfo allField = catalogType.GetField("All", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                IEnumerable all = allField == null ? null : allField.GetValue(null) as IEnumerable;
                if (all == null)
                    return result;
                foreach (object raw in all)
                {
                    if (raw == null) continue;
                    Type t = raw.GetType();
                    string careerId = Convert.ToString(GetMemberValue(raw, t, "CareerId"));
                    if (String.IsNullOrEmpty(careerId)) continue;
                    result.Add(new RuntimeDefinition
                    {
                        Raw = raw,
                        CareerId = careerId,
                        MapName = Convert.ToString(GetMemberValue(raw, t, "MapName")),
                        Kind = Convert.ToString(GetMemberValue(raw, t, "Kind"))
                    });
                }
            }
            catch { }
            return result;
        }

        private static RuntimeDefinition GetDefinition(string careerId)
        {
            if (String.IsNullOrEmpty(careerId))
                return null;
            List<RuntimeDefinition> definitions = GetDefinitions();
            for (int i = 0; i < definitions.Count; i++)
                if (String.Equals(definitions[i].CareerId, careerId, StringComparison.Ordinal))
                    return definitions[i];
            return null;
        }

        private static object GetMemberValue(object instance, Type type, string name)
        {
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(instance);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(instance, null);
        }

        private static object GetFieldValue(object instance, string name)
        {
            if (instance == null) return null;
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field == null ? null : field.GetValue(instance);
        }

        private static MethodInfo FindMethod(Type type, string name, bool isStatic)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            MethodInfo[] methods = type.GetMethods(flags);
            for (int i = 0; i < methods.Length; i++)
                if (String.Equals(methods[i].Name, name, StringComparison.Ordinal))
                    return methods[i];
            return null;
        }

        private static bool IsEncounterHeroAvailable(object behavior, string careerId)
        {
            try
            {
                MethodInfo method = FindMethod(behavior.GetType(), "IsEncounterHeroAvailable", false);
                if (method == null) return false;
                object[] args = { careerId, null };
                object value = method.Invoke(behavior, args);
                return value is bool && (bool)value;
            }
            catch { return false; }
        }

        private static Hero GetActiveEncounterHero(string careerId)
        {
            object behavior = GetMainBehavior();
            if (behavior == null) return null;
            try
            {
                MethodInfo method = FindMethod(behavior.GetType(), "TryGetActiveEncounterHero", false);
                if (method == null) return null;
                object[] args = { careerId, null };
                object value = method.Invoke(behavior, args);
                if (!(value is bool) || !(bool)value) return null;
                return args[1] as Hero;
            }
            catch { return null; }
        }

        private static MobileParty FindActiveEncounter(object behavior, string careerId)
        {
            try
            {
                MethodInfo method = FindMethod(behavior.GetType(), "FindActiveEncounter", false);
                return method == null ? null : method.Invoke(behavior, new object[] { careerId }) as MobileParty;
            }
            catch { return null; }
        }

        private static Settlement FindGuardianSite(object behavior, object rawDefinition)
        {
            try
            {
                MethodInfo method = FindMethod(behavior.GetType(), "FindGuardianSite", false);
                return method == null ? null : method.Invoke(behavior, new object[] { rawDefinition }) as Settlement;
            }
            catch { return null; }
        }

        private static Settlement FindNearestTrackableSettlement(Type behaviorType, CampaignVec2 position)
        {
            try
            {
                MethodInfo method = FindMethod(behaviorType, "FindNearestTrackableSettlement", true);
                return method == null ? null : method.Invoke(null, new object[] { position }) as Settlement;
            }
            catch { return null; }
        }

        private static bool TryTrackOnMap(Type behaviorType, ITrackableCampaignObject target)
        {
            try
            {
                MethodInfo method = FindMethod(behaviorType, "TryTrackOnMap", true);
                if (method == null) return false;
                object value = method.Invoke(null, new object[] { target });
                return value is bool && (bool)value;
            }
            catch { return false; }
        }

        private static string GetFallbackLeaderName(string careerId)
        {
            try
            {
                Type profiles = Type.GetType("TORCareerUniques.EncounterHeroProfiles, TORCareerUniques", false);
                MethodInfo get = profiles == null ? null : FindMethod(profiles, "Get", true);
                object profile = get == null ? null : get.Invoke(null, new object[] { careerId });
                if (profile != null)
                {
                    object fullName = GetMemberValue(profile, profile.GetType(), "FullName");
                    string text = Convert.ToString(fullName);
                    if (!String.IsNullOrEmpty(text)) return text;
                }
            }
            catch { }
            return "the encounter's leader";
        }

        private static string Slug(string value)
        {
            if (String.IsNullOrEmpty(value)) return "unknown";
            return value.Replace(' ', '_').Replace('-', '_').ToLowerInvariant();
        }
    }
}
