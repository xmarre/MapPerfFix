using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace MapPerfFix
{
    public class SubModule : MBSubModuleBase
    {
        internal static readonly Harmony Harmony = new Harmony("mmq.mapperffix");
        internal static readonly ConcurrentQueue<InformationMessage> MsgQueue = new ConcurrentQueue<InformationMessage>();
        internal static DateTime LastFlushUtc = DateTime.UtcNow;

        protected override void OnSubModuleLoad()
        {
            Harmony.Patch(
                AccessTools.Method(typeof(InformationManager), nameof(InformationManager.DisplayMessage),
                    new Type[] { typeof(InformationMessage) }),
                prefix: new HarmonyMethod(typeof(SubModule), nameof(DisplayMessage_Prefix)));
        }

        // Buffer UI messages to smooth daily bursts.
        public static bool DisplayMessage_Prefix(InformationMessage msg)
        {
            // Only buffer on campaign map. In missions, let it pass through.
            if (GameStateManager.Current != null &&
                GameStateManager.Current.ActiveState is MapState)
            {
                MsgQueue.Enqueue(msg);
                return false;
            }
            return true;
        }

        // Flush few messages per real-time second to avoid spikes.
        internal static void TryFlushBufferedMessages(int maxPerFlush = 12)
        {
            var now = DateTime.UtcNow;
            if ((now - LastFlushUtc).TotalMilliseconds < 800) return;
            LastFlushUtc = now;
            int n = 0;
            InformationMessage m;
            while (n < maxPerFlush && MsgQueue.TryDequeue(out m))
            {
                InformationManager.DisplayMessage(m);
                n++;
            }
        }

        protected override void OnGameStart(Game game, IGameStarter starterObj)
        {
            if (game.GameType is Campaign)
                (starterObj as CampaignGameStarter)?.AddBehavior(new MapPerfFixBehavior());
        }
    }

    internal sealed class MapPerfFixBehavior : CampaignBehaviorBase
    {
        // Limits. Tune to taste.
        private const int BanditCapGlobal = 160;    // hard cap across map
        private const int BanditCapNearPlayer = 60; // within 250 map units of player
        private const int CaravanCapPerClan = 2;    // caravans per non-player clan
        private CampaignTime _nextDiag = CampaignTime.Hours(1f);

        public override void RegisterEvents()
        {
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, OnHourly);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDaily);
            CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, OnLoaded);
            // Light per-frame smoother while on map
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnTick);
        }

        public override void SyncData(IDataStore ds) { /* no state */ }

        private void OnLoaded(CampaignGameStarter _)
        {
            InformationManager.DisplayMessage(new InformationMessage("[MapPerfFix] Loaded."));
        }

        private void OnTick(float dt)
        {
            // Drain message buffer a bit each real-time second.
            SubModule.TryFlushBufferedMessages(10);
        }

        private void OnHourly()
        {
            try
            {
                LimitBandits();
                LimitCaravans();
                if (CampaignTime.Now >= _nextDiag)
                {
                    _nextDiag = CampaignTime.Now + CampaignTime.Hours(3f);
                    DiagnosticsPing();
                }
            }
            catch (Exception ex)
            {
                InformationManager.DisplayMessage(new InformationMessage("[MapPerfFix] Hourly error: " + ex.Message));
            }
        }

        private void OnDaily()
        {
            // Extra flush after daily economy tick, when stutters often occur.
            SubModule.TryFlushBufferedMessages(40);
        }

        private void DiagnosticsPing()
        {
            var all = MobileParty.All.Where(p => p?.Party != null && p.IsActive).ToList();
            int total = all.Count;
            int bandits = all.Count(p => p.IsBandit);
            int caravans = all.Count(p => p.IsCaravan);
            int armies = Army.Armies?.Count(a => a?.IsActive == true) ?? 0;

            InformationManager.DisplayMessage(new InformationMessage(
                $"[MPF] Parties: {total} | Bandits: {bandits} | Caravans: {caravans} | Armies: {armies}"));
        }

        private void LimitBandits()
        {
            var all = MobileParty.All.Where(p => p?.Party != null && p.IsActive && p.IsBandit).ToList();
            if (all.Count <= BanditCapGlobal) return;

            var player = MobileParty.MainParty;
            var nearList = all.Where(p => p.Position2D.Distance(player.Position2D) < 250f).ToList();
            var farList = all.Except(nearList).ToList();

            // First trim far bandits until global cap met.
            int toCull = Math.Max(0, all.Count - BanditCapGlobal);
            if (toCull > 0)
            {
                foreach (var p in farList
                             .OrderByDescending(p => p.Party.MemberRoster.TotalManCount)
                             .ThenByDescending(p => p.Party.PrisonRoster.TotalManCount)
                             .Take(toCull))
                {
                    SafeDestroy(p);
                }
            }

            // Then enforce a softer local cap near player.
            if (nearList.Count > BanditCapNearPlayer)
            {
                int trim = nearList.Count - BanditCapNearPlayer;
                foreach (var p in nearList
                             .OrderByDescending(p => p.Position2D.Distance(player.Position2D))
                             .ThenByDescending(p => p.Party.MemberRoster.TotalManCount)
                             .Take(trim))
                {
                    SafeDestroy(p);
                }
            }
        }

        private void LimitCaravans()
        {
            // Too many caravans can spike economy ticks and pathfinding.
            var caravans = MobileParty.All.Where(p => p?.Party != null && p.IsActive && p.IsCaravan).ToList();
            foreach (var grp in caravans.GroupBy(c => c?.Owner?.Clan)
                                        .Where(g => g.Key != null && !g.Key.IsPlayerClan && g.Count() > CaravanCapPerClan))
            {
                foreach (var c in grp.OrderByDescending(c => c.Position2D.Distance(MobileParty.MainParty.Position2D))
                                     .Skip(CaravanCapPerClan))
                {
                    SafeDisbandCaravan(c);
                }
            }
        }

        private void SafeDestroy(MobileParty p)
        {
            try { DestroyPartyAction.Apply(null, p.Party); }
            catch { /* ignore */ }
        }

        private void SafeDisbandCaravan(MobileParty p)
        {
            try { DestroyPartyAction.Apply(null, p.Party); }
            catch { /* ignore */ }
        }
    }
}
