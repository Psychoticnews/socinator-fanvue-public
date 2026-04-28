using System;
using System.IO;
using System.Windows.Threading;
using DominatorHouseCore.LogHelper;
using FanvueDominatorCore.Models;
using Newtonsoft.Json;
using Prism.Mvvm;

namespace FanvueDominatorUI.ViewModels.Settings
{
    public class NotificationSettingsViewModel : BindableBase
    {
        private const string LogTag = "[FanvueNotificationSettings]";
        private const string SettingsFileName = "FanvueNotificationSettings.json";

        private readonly DispatcherTimer _saveDebounce;
        private bool _loaded;

        private int _pollIntervalSeconds;
        public int PollIntervalSeconds
        {
            get { return _pollIntervalSeconds; }
            set
            {
                int clamped = value < 30 ? 30 : value;
                if (clamped > 300) { clamped = 300; }
                if (SetProperty(ref _pollIntervalSeconds, clamped) && _loaded) { ScheduleSave(); }
            }
        }

        private bool _notifyNewMessage;
        public bool NotifyNewMessage
        {
            get { return _notifyNewMessage; }
            set { if (SetProperty(ref _notifyNewMessage, value) && _loaded) { ScheduleSave(); } }
        }

        private bool _notifyNewSubscriber;
        public bool NotifyNewSubscriber
        {
            get { return _notifyNewSubscriber; }
            set { if (SetProperty(ref _notifyNewSubscriber, value) && _loaded) { ScheduleSave(); } }
        }

        private bool _notifyNewFollower;
        public bool NotifyNewFollower
        {
            get { return _notifyNewFollower; }
            set { if (SetProperty(ref _notifyNewFollower, value) && _loaded) { ScheduleSave(); } }
        }

        private bool _notifyNewTip;
        public bool NotifyNewTip
        {
            get { return _notifyNewTip; }
            set { if (SetProperty(ref _notifyNewTip, value) && _loaded) { ScheduleSave(); } }
        }

        private bool _autoMarkReadOnSelection;
        public bool AutoMarkReadOnSelection
        {
            get { return _autoMarkReadOnSelection; }
            set { if (SetProperty(ref _autoMarkReadOnSelection, value) && _loaded) { ScheduleSave(); } }
        }

        private string _defaultMassMessageAudience;
        public string DefaultMassMessageAudience
        {
            get { return _defaultMassMessageAudience; }
            set { if (SetProperty(ref _defaultMassMessageAudience, value) && _loaded) { ScheduleSave(); } }
        }

        public NotificationSettingsViewModel()
        {
            _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveDebounce.Tick += OnSaveDebounceTick;
            Load();
            _loaded = true;
        }

        private static string GetSettingsPath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var socinatorPath = Path.Combine(appDataPath, "Socinator1.0");
            if (!Directory.Exists(socinatorPath)) { Directory.CreateDirectory(socinatorPath); }
            return Path.Combine(socinatorPath, SettingsFileName);
        }

        public FanvueNotificationSettings ToModel()
        {
            return new FanvueNotificationSettings
            {
                PollIntervalSeconds = _pollIntervalSeconds,
                NotifyNewMessage = _notifyNewMessage,
                NotifyNewSubscriber = _notifyNewSubscriber,
                NotifyNewFollower = _notifyNewFollower,
                NotifyNewTip = _notifyNewTip,
                AutoMarkReadOnSelection = _autoMarkReadOnSelection,
                DefaultMassMessageAudience = _defaultMassMessageAudience
            };
        }

        public static FanvueNotificationSettings LoadFromDisk()
        {
            try
            {
                var path = GetSettingsPath();
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var loaded = JsonConvert.DeserializeObject<FanvueNotificationSettings>(json);
                    if (loaded != null) { return loaded; }
                }
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Warn(LogTag + " Load failed: " + ex.Message);
            }
            return new FanvueNotificationSettings();
        }

        private void Load()
        {
            var s = LoadFromDisk();
            _pollIntervalSeconds = s.PollIntervalSeconds < 30 ? 60 : s.PollIntervalSeconds;
            _notifyNewMessage = s.NotifyNewMessage;
            _notifyNewSubscriber = s.NotifyNewSubscriber;
            _notifyNewFollower = s.NotifyNewFollower;
            _notifyNewTip = s.NotifyNewTip;
            _autoMarkReadOnSelection = s.AutoMarkReadOnSelection;
            _defaultMassMessageAudience = s.DefaultMassMessageAudience;
        }

        private void ScheduleSave()
        {
            try
            {
                _saveDebounce.Stop();
                _saveDebounce.Start();
            }
            catch { }
        }

        private void OnSaveDebounceTick(object sender, EventArgs e)
        {
            try
            {
                _saveDebounce.Stop();
                Save();
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Warn(LogTag + " Save tick failed: " + ex.Message);
            }
        }

        private void Save()
        {
            try
            {
                var path = GetSettingsPath();
                var json = JsonConvert.SerializeObject(ToModel(), Formatting.Indented);
                File.WriteAllText(path, json);
                GlobusLogHelper.log.Info(LogTag + " Saved (interval=" + _pollIntervalSeconds + "s)");
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Warn(LogTag + " Save failed: " + ex.Message);
            }
        }
    }
}
