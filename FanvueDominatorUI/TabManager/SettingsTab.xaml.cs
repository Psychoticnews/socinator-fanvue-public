using DominatorHouseCore.Models;
using DominatorUIUtility.CustomControl;
using FanvueDominatorUI.Views.Settings;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace FanvueDominatorUI.TabManager
{
    /// <summary>
    /// Settings tab for Fanvue module.
    /// Contains blacklist/whitelist, chatter manager and other module settings.
    /// </summary>
    public partial class SettingsTab : UserControl
    {
        private static SettingsTab _instance;

        public SettingsTab()
        {
            InitializeComponent();

            var tabItems = new List<TabItemTemplates>
            {
                new TabItemTemplates
                {
                    Title = Application.Current.FindResource("LangKeyBlacklistusers")?.ToString() ?? "Blacklist Users",
                    Content = new Lazy<UserControl>(() => new BlacklistUserControl())
                },
                new TabItemTemplates
                {
                    Title = Application.Current.FindResource("LangKeyWhitelistUsers")?.ToString() ?? "Whitelist Users",
                    Content = new Lazy<UserControl>(() => new WhitelistuserControl())
                },
                new TabItemTemplates
                {
                    Title = "Chatter Manager",
                    Content = new Lazy<UserControl>(() => new ChatterManagerControl())
                },
                new TabItemTemplates
                {
                    Title = "Notifications & Chat",
                    Content = new Lazy<UserControl>(() => new NotificationSettingsControl())
                }
            };
            SettingsTabs.ItemsSource = tabItems;
        }

        public static SettingsTab GetSingletonSettingsTab()
        {
            return _instance ?? (_instance = new SettingsTab());
        }
    }
}
