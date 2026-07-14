using System;
using System.IO;
using System.Reflection;

using HarmonyLib;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;
using TaleWorlds.SaveSystem;
using TaleWorlds.SaveSystem.Load;

namespace BannerlordPlayerSettlement
{
    public class SaveHandler
    {
        public enum SaveMechanism
        {
            Overwrite = 0,
            Auto = 1,
            Temporary = 2
        }

        private static readonly SaveHandler _instance = new SaveHandler();
        public static SaveHandler Instance => _instance;

        private static readonly PropertyInfo ActiveSaveSlotNameProp = AccessTools.Property(typeof(MBSaveLoad), "ActiveSaveSlotName");
        private static readonly MethodInfo GetNextAvailableSaveNameMethod = AccessTools.Method(typeof(MBSaveLoad), "GetNextAvailableSaveName");

        private bool _saveLoadInProgress;
        private bool _exitTickPending;
        private bool _awaitingCampaignShutdown;
        private bool _loadStarted;
        private int _deferredExitTicks;
        private int _postShutdownTicks;
        private SaveMechanism _pendingSaveMechanism;
        private string? _pendingOriginalSaveName;
        private string? _pendingNewSaveGameName;

        public static void SaveLoad(SaveMechanism saveMechanism = SaveMechanism.Overwrite, Action<SaveMechanism, string>? afterSave = null)
        {
            Instance.SaveAndLoad(saveMechanism, afterSave);
        }

        public static void SaveOnly(bool overwrite = true)
        {
            Instance.Save(overwrite);
        }

        public void SaveAndLoad(SaveMechanism saveMechanism = SaveMechanism.Overwrite, Action<SaveMechanism, string>? afterSave = null)
        {
            if (_saveLoadInProgress)
            {
                WriteTransitionLog("SaveAndLoad ignored because a transition is already active");
                return;
            }

            string saveName = (string)ActiveSaveSlotNameProp.GetValue(null);
            if (saveName == null)
            {
                saveName = (string)GetNextAvailableSaveNameMethod.Invoke(null, Array.Empty<object>());
                ActiveSaveSlotNameProp.SetValue(null, saveName);
            }

            _saveLoadInProgress = true;
            WriteTransitionLog($"Save requested: mechanism={saveMechanism}, original={saveName}");

            CampaignEvents.OnSaveOverEvent.ClearListeners(this);
            CampaignEvents.OnSaveOverEvent.AddNonSerializedListener(this,
                new Action<bool, string>((successful, writtenName) =>
                    ApplyInternal(saveMechanism, saveName, successful, writtenName, afterSave)));

            try
            {
                if (saveMechanism == SaveMechanism.Overwrite)
                {
                    Campaign.Current.SaveHandler.SaveAs(saveName);
                }
                else
                {
                    Campaign.Current.SaveHandler.SaveAs(saveName + new TextObject("{=player_settlement_n_02} (auto)").ToString());
                }
            }
            catch (Exception e)
            {
                WriteTransitionLog("Save request failed: " + e);
                ResetPendingState();
                throw;
            }
        }

        public void Save(bool overwrite = true)
        {
            string saveName = (string)ActiveSaveSlotNameProp.GetValue(null);
            if (saveName == null)
            {
                saveName = (string)GetNextAvailableSaveNameMethod.Invoke(null, Array.Empty<object>());
                ActiveSaveSlotNameProp.SetValue(null, saveName);
            }

            if (overwrite)
            {
                Campaign.Current.SaveHandler.SaveAs(saveName);
            }
            else
            {
                Campaign.Current.SaveHandler.SaveAs(saveName + new TextObject("{=player_settlement_n_02} (auto)").ToString());
            }
        }

        private void ApplyInternal(
            SaveMechanism saveMechanism,
            string originalSaveName,
            bool isSaveSuccessful,
            string newSaveGameName,
            Action<SaveMechanism, string>? afterSave = null)
        {
            CampaignEvents.OnSaveOverEvent.ClearListeners(this);
            WriteTransitionLog($"OnSaveOver: success={isSaveSuccessful}, written={newSaveGameName}");

            if (!isSaveSuccessful)
            {
                ResetPendingState();
                return;
            }

            try
            {
                afterSave?.Invoke(saveMechanism, newSaveGameName);
            }
            catch (Exception e)
            {
                WriteTransitionLog("afterSave callback failed: " + e);
            }

            _pendingSaveMechanism = saveMechanism;
            _pendingOriginalSaveName = originalSaveName;
            _pendingNewSaveGameName = newSaveGameName;
            _deferredExitTicks = 3;
            _exitTickPending = true;

            CampaignEvents.TickEvent.ClearListeners(this);
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnDeferredExitTick);
            WriteTransitionLog("Waiting for campaign ticks before requesting EndGame");
        }

        private void OnDeferredExitTick(float dt)
        {
            if (!_exitTickPending)
            {
                CampaignEvents.TickEvent.ClearListeners(this);
                return;
            }

            if (_deferredExitTicks-- > 0)
            {
                return;
            }

            CampaignEvents.TickEvent.ClearListeners(this);
            _exitTickPending = false;
            _awaitingCampaignShutdown = true;
            _postShutdownTicks = 5;

            WriteTransitionLog("Requesting MBGameManager.EndGame");
            MBGameManager.EndGame();
        }

        public void OnApplicationTick(float dt)
        {
            if (!_awaitingCampaignShutdown || _loadStarted)
            {
                return;
            }

            // StartNewGame must never run while the old Game or GameManager still exists.
            if (Game.Current != null || GameManagerBase.Current != null)
            {
                return;
            }

            // Give the global state manager and initial screen a few frames to settle.
            if (_postShutdownTicks-- > 0)
            {
                return;
            }

            _loadStarted = true;
            WriteTransitionLog("Old campaign fully disposed; beginning load from global state context");
            BeginLoadFromInitialState();
        }

        private void BeginLoadFromInitialState()
        {
            string newSaveGameName = _pendingNewSaveGameName ?? string.Empty;
            SaveGameFileInfo saveFileWithName = MBSaveLoad.GetSaveFileWithName(newSaveGameName);
            if (saveFileWithName == null || saveFileWithName.IsCorrupted)
            {
                WriteTransitionLog("Saved game could not be found or is marked corrupt: " + newSaveGameName);
                ResetPendingState();
                InformationManager.ShowInquiry(new InquiryData(
                    new TextObject("{=oZrVNUOk}Error").ToString(),
                    new TextObject("{=t6W3UjG0}Save game file appear to be corrupted. Try starting a new campaign or load another one from Saved Games menu.").ToString(),
                    true,
                    false,
                    new TextObject("{=yS7PvrTD}OK").ToString(),
                    null,
                    null,
                    null,
                    "",
                    0f,
                    null,
                    null,
                    null), false, false);
                return;
            }

            try
            {
                SandBoxSaveHelper.TryLoadSave(
                    saveFileWithName,
                    new Action<LoadResult>(StartGameFromInitialState),
                    () =>
                    {
                        WriteTransitionLog("Load cancelled");
                        ResetPendingState();
                    });
            }
            catch (Exception e)
            {
                WriteTransitionLog("Begin load failed: " + e);
                ResetPendingState();
                throw;
            }
        }

        private void StartGameFromInitialState(LoadResult loadResult)
        {
            try
            {
                string originalSaveName = _pendingOriginalSaveName ?? string.Empty;
                string newSaveGameName = _pendingNewSaveGameName ?? string.Empty;
                SaveMechanism saveMechanism = _pendingSaveMechanism;

                if (saveMechanism == SaveMechanism.Temporary)
                {
                    MBSaveLoad.DeleteSaveGame(newSaveGameName);
                    SaveGameFileInfo originalSave = MBSaveLoad.GetSaveFileWithName(originalSaveName);
                    if (originalSave != null && !originalSave.IsCorrupted)
                    {
                        ActiveSaveSlotNameProp.SetValue(null, originalSaveName);
                    }
                }

                WriteTransitionLog("Starting saved campaign from global state context");
                ResetPendingState(clearCampaignListeners: false);
                MBSaveLoad.OnStartGame(loadResult);
                MBGameManager.StartNewGame(new SandBoxGameManager(loadResult));
            }
            catch (Exception e)
            {
                WriteTransitionLog("Start saved campaign failed: " + e);
                ResetPendingState(clearCampaignListeners: false);
                throw;
            }
        }

        private void ResetPendingState(bool clearCampaignListeners = true)
        {
            if (clearCampaignListeners)
            {
                try { CampaignEvents.OnSaveOverEvent.ClearListeners(this); } catch { }
                try { CampaignEvents.TickEvent.ClearListeners(this); } catch { }
            }

            _saveLoadInProgress = false;
            _exitTickPending = false;
            _awaitingCampaignShutdown = false;
            _loadStarted = false;
            _deferredExitTicks = 0;
            _postShutdownTicks = 0;
            _pendingOriginalSaveName = null;
            _pendingNewSaveGameName = null;
        }

        private static void WriteTransitionLog(string message)
        {
            try
            {
                string userDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Mount and Blade II Bannerlord");
                string logDirectory = Path.Combine(userDir, "Configs", "BannerlordPlayerSettlement");
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(
                    Path.Combine(logDirectory, "save_transition.log"),
                    DateTime.UtcNow.ToString("O") + " | " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
