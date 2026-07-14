using System;
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
        private bool _deferredLoadPending;
        private int _deferredLoadTicks;
        private SaveMechanism _deferredSaveMechanism;
        private string? _deferredOriginalSaveName;
        private string? _deferredNewSaveGameName;

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
                return;
            }

            string saveName = (string)ActiveSaveSlotNameProp.GetValue(null);
            if (saveName == null)
            {
                saveName = (string)GetNextAvailableSaveNameMethod.Invoke(null, Array.Empty<object>());
                ActiveSaveSlotNameProp.SetValue(null, saveName);
            }

            _saveLoadInProgress = true;
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
            catch
            {
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

            if (!isSaveSuccessful)
            {
                ResetPendingState();
                return;
            }

            try
            {
                afterSave?.Invoke(saveMechanism, newSaveGameName);
            }
            catch
            {
            }

            _deferredSaveMechanism = saveMechanism;
            _deferredOriginalSaveName = originalSaveName;
            _deferredNewSaveGameName = newSaveGameName;
            _deferredLoadTicks = 3;
            _deferredLoadPending = true;

            CampaignEvents.TickEvent.ClearListeners(this);
            CampaignEvents.TickEvent.AddNonSerializedListener(this, OnDeferredLoadTick);
        }

        private void OnDeferredLoadTick(float dt)
        {
            if (!_deferredLoadPending)
            {
                CampaignEvents.TickEvent.ClearListeners(this);
                return;
            }

            if (_deferredLoadTicks-- > 0)
            {
                return;
            }

            CampaignEvents.TickEvent.ClearListeners(this);

            SaveMechanism saveMechanism = _deferredSaveMechanism;
            string originalSaveName = _deferredOriginalSaveName ?? string.Empty;
            string newSaveGameName = _deferredNewSaveGameName ?? string.Empty;

            _deferredLoadPending = false;
            _deferredOriginalSaveName = null;
            _deferredNewSaveGameName = null;

            BeginDeferredLoad(saveMechanism, originalSaveName, newSaveGameName);
        }

        private void BeginDeferredLoad(SaveMechanism saveMechanism, string originalSaveName, string newSaveGameName)
        {
            SaveGameFileInfo saveFileWithName = MBSaveLoad.GetSaveFileWithName(newSaveGameName);
            if (saveFileWithName != null && !saveFileWithName.IsCorrupted)
            {
                SandBoxSaveHelper.TryLoadSave(saveFileWithName, new Action<LoadResult>(loadResult =>
                {
                    if (saveMechanism == SaveMechanism.Temporary)
                    {
                        MBSaveLoad.DeleteSaveGame(newSaveGameName);
                        SaveGameFileInfo originalSave = MBSaveLoad.GetSaveFileWithName(originalSaveName);
                        if (originalSave != null && !originalSave.IsCorrupted)
                        {
                            ActiveSaveSlotNameProp.SetValue(null, originalSaveName);
                        }
                    }

                    _saveLoadInProgress = false;
                    StartGame(loadResult);
                }), ResetPendingState);
                return;
            }

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
        }

        private void ResetPendingState()
        {
            CampaignEvents.OnSaveOverEvent.ClearListeners(this);
            CampaignEvents.TickEvent.ClearListeners(this);
            _saveLoadInProgress = false;
            _deferredLoadPending = false;
            _deferredLoadTicks = 0;
            _deferredOriginalSaveName = null;
            _deferredNewSaveGameName = null;
        }

        public void StartGame(LoadResult loadResult)
        {
            if (Game.Current != null)
            {
                GameStateManager.Current.CleanStates(0);
                GameStateManager.Current = TaleWorlds.MountAndBlade.Module.CurrentModule.GlobalGameStateManager;
            }

            MBSaveLoad.OnStartGame(loadResult);
            MBGameManager.StartNewGame(new SandBoxGameManager(loadResult));
        }
    }
}
