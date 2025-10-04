using System;
using System.Threading;
using TaleWorlds.CampaignSystem;

namespace MapPerfProbe
{
    internal static class InitGate
    {
        private static readonly object LoadOwner = new object();
        private static readonly object NewGameOwner = new object();
        private static readonly object SessionOwner = new object();
        private static int _wired;

        internal static volatile bool Ready;

        internal static void Wire()
        {
            if (Interlocked.Exchange(ref _wired, 1) == 1)
                return;

            Ready = false;

            try { CampaignEvents.OnGameLoadFinishedEvent.AddNonSerializedListener(LoadOwner, OnReady); }
            catch { /* best effort */ }

            try { CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(NewGameOwner, _ => OnReady()); }
            catch { /* best effort */ }

            try { CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(SessionOwner, _ => OnReady()); }
            catch { /* best effort */ }
        }

        internal static void Reset()
        {
            Ready = false;
        }

        private static void OnReady()
        {
            Ready = true;
        }

        internal static bool MapReady()
        {
            if (!Ready)
                return false;

            try
            {
                var campaign = Campaign.Current;
                if (campaign == null)
                    return false;

                _ = campaign.TimeControlMode; // touch to ensure Campaign.Current is valid
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
