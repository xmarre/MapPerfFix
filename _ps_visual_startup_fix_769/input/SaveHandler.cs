using System;
using System.IO;
using System.Reflection;

using BannerlordPlayerSettlement.Behaviours;

using HarmonyLib;

using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;

namespace BannerlordPlayerSettlement
{
    public sealed class SaveHandler
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

        private bool _saveInProgress;
        private Action<SaveMechanism, string>? _afterSave;
        private SaveMechanism _requestedMechanism;
        private string? _requestedSaveName;

        public static void SaveLoad(SaveMechanism saveMechanism = SaveMechanism.Overwrite, Action<SaveMechanism, string>? afterSave = null)
        {
            Instance.SaveAndContinue(saveMechanism, afterSave);
        }

        public static void SaveOnly(bool overwrite = true)
        {
            Instance.Save(overwrite);
        }

        public void SaveAndContinue(SaveMechanism saveMechanism = SaveMechanism.Overwrite, Action<SaveMechanism, string>? afterSave = null)
        {
            if (_saveInProgress)
            {
                WriteTransitionLog("Save request ignored because another placement save is active");
                return;
            }

            if (Campaign.Current?.SaveHandler == null)
            {
                WriteTransitionLog("Save request ignored because Campaign.SaveHandler is unavailable");
                return;
            }

            try
            {
                PlayerSettlementBehaviour.Instance?.Reset();
                WriteTransitionLog("Completed placement state cleared before save");
            }
            catch (Exception e)
            {
                WriteTransitionLog("Placement reset before save failed: " + e);
            }

            string activeName = ActiveSaveSlotNameProp.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(activeName))
            {
                activeName = GetNextAvailableSaveNameMethod.Invoke(null, Array.Empty<object>()) as string;
                if (string.IsNullOrWhiteSpace(activeName))
                {
                    activeName = "PlayerSettlement";
                }
                ActiveSaveSlotNameProp.SetValue(null, activeName);
            }

            string targetName;
            SaveMechanism effectiveMechanism;
            if (saveMechanism == SaveMechanism.Auto)
            {
                targetName = activeName + new TextObject("{=player_settlement_n_02} (auto)").ToString();
                effectiveMechanism = SaveMechanism.Auto;
            }
            else
            {
                targetName = activeName;
                effectiveMechanism = SaveMechanism.Overwrite;
            }

            _saveInProgress = true;
            _afterSave = afterSave;
            _requestedMechanism = effectiveMechanism;
            _requestedSaveName = targetName;

            CampaignEvents.OnSaveOverEvent.ClearListeners(this);
            CampaignEvents.OnSaveOverEvent.AddNonSerializedListener(this, OnSaveCompleted);

            WriteTransitionLog($"Saving placement without in-process reload: requested={saveMechanism}, effective={effectiveMechanism}, target={targetName}");

            try
            {
                Campaign.Current.SaveHandler.SaveAs(targetName);
            }
            catch (Exception e)
            {
                WriteTransitionLog("Placement save request failed: " + e);
                ResetPendingState();
                throw;
            }
        }

        public void Save(bool overwrite = true)
        {
            string activeName = ActiveSaveSlotNameProp.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(activeName))
            {
                activeName = GetNextAvailableSaveNameMethod.Invoke(null, Array.Empty<object>()) as string;
                if (string.IsNullOrWhiteSpace(activeName))
                {
                    activeName = "PlayerSettlement";
                }
                ActiveSaveSlotNameProp.SetValue(null, activeName);
            }

            string targetName = overwrite
                ? activeName
                : activeName + new TextObject("{=player_settlement_n_02} (auto)").ToString();
            Campaign.Current.SaveHandler.SaveAs(targetName);
        }

        private void OnSaveCompleted(bool successful, string writtenName)
        {
            CampaignEvents.OnSaveOverEvent.ClearListeners(this);
            WriteTransitionLog($"Placement save completed: success={successful}, written={writtenName}");

            Action<SaveMechanism, string>? callback = _afterSave;
            SaveMechanism mechanism = _requestedMechanism;
            string requestedName = _requestedSaveName ?? writtenName;
            ResetPendingState(clearListener: false);

            if (!successful)
            {
                MBInformationManager.AddQuickInformation(
                    new TextObject("{=!}Player Settlement could not save the newly created settlement."),
                    0,
                    Hero.MainHero?.CharacterObject);
                return;
            }

            try
            {
                ActiveSaveSlotNameProp.SetValue(null, string.IsNullOrWhiteSpace(writtenName) ? requestedName : writtenName);
            }
            catch (Exception e)
            {
                WriteTransitionLog("Could not update active save slot: " + e);
            }

            try
            {
                callback?.Invoke(mechanism, writtenName);
            }
            catch (Exception e)
            {
                WriteTransitionLog("afterSave callback failed: " + e);
            }

            MBInformationManager.AddQuickInformation(
                new TextObject("{=!}Settlement created and saved. Automatic in-process reload is disabled for Bannerlord 1.3.15 stability."),
                0,
                Hero.MainHero?.CharacterObject);
        }

        public void OnApplicationTick(float dt)
        {
        }

        private void ResetPendingState(bool clearListener = true)
        {
            if (clearListener)
            {
                try { CampaignEvents.OnSaveOverEvent.ClearListeners(this); } catch { }
            }

            _saveInProgress = false;
            _afterSave = null;
            _requestedSaveName = null;
            _requestedMechanism = SaveMechanism.Overwrite;
        }

        private static void WriteTransitionLog(string message)
        {
            try
            {
                string userDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                    "Mount and Blade II Bannerlord");
                string directory = Path.Combine(userDir, "Configs", "BannerlordPlayerSettlement");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "save_transition.log"),
                    DateTime.UtcNow.ToString("O") + " | " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
