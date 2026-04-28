using DominatorHouseCore.Models;
using FanvueDominatorUI.Views.Engage;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace FanvueDominatorUI.TabManager
{
    /// <summary>
    /// Engagement tab for Fanvue module.
    /// Hosts Chat Monitor, Mass Messages and Vault sub-tabs.
    /// </summary>
    public partial class EngageTab : UserControl
    {
        private static EngageTab _instance;

        public EngageTab()
        {
            InitializeComponent();

            var items = new List<TabItemTemplates>
            {
                new TabItemTemplates
                {
                    Title = "Chat Monitor",
                    Content = new Lazy<UserControl>(() => new ChatMonitorControl())
                },
                new TabItemTemplates
                {
                    Title = "Mass Messages",
                    Content = new Lazy<UserControl>(() => new MassMessagesControl())
                },
                new TabItemTemplates
                {
                    Title = "Vault",
                    Content = new Lazy<UserControl>(() => new VaultControl())
                }
            };
            EngageTabs.ItemsSource = items;
        }

        public static EngageTab GetSingletonObject()
        {
            return _instance ?? (_instance = new EngageTab());
        }
    }
}
