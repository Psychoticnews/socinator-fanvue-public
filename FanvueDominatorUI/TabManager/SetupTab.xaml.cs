using DominatorHouseCore.Models;
using FanvueDominatorUI.Controls.Setup;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace FanvueDominatorUI.TabManager
{
    /// <summary>
    /// Setup tab for Fanvue module.
    /// Contains API configuration, OAuth connection, and setup wizard.
    /// </summary>
    public partial class SetupTab : UserControl
    {
        private static SetupTab _instance;

        public SetupTab()
        {
            InitializeComponent();

            var tabItems = new List<TabItemTemplates>
            {
                new TabItemTemplates
                {
                    Title = "Connect Account",
                    Content = new Lazy<UserControl>(() => new FanvueSetupWizard())
                }
            };
            SetupTabs.ItemsSource = tabItems;
        }

        public static SetupTab GetSingletonSetupTab()
        {
            return _instance ?? (_instance = new SetupTab());
        }
    }
}
