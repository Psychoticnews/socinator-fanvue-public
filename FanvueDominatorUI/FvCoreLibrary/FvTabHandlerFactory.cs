using DominatorHouseCore.Enums;
using DominatorHouseCore.Interfaces;
using DominatorHouseCore.LogHelper;
using DominatorHouseCore.Models;
using FanvueDominatorUI.TabManager;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace FanvueDominatorUI.FvCoreLibrary
{
    /// <summary>
    /// Tab handler factory for Fanvue module.
    /// Creates and manages the tabs displayed in the Fanvue section.
    /// </summary>
    public class FvTabHandlerFactory : ITabHandlerFactory
    {
        private static FvTabHandlerFactory _instance;
        private readonly AccessorStrategies _strategies;

        private FvTabHandlerFactory(AccessorStrategies strategies)
        {
            _strategies = strategies;
            TabInitializer(strategies);
            NetworkName = $"{SocialNetworks.Fanvue}  Dominator";
        }

        public string NetworkName { get; set; }
        public List<TabItemTemplates> NetworkTabs { get; set; }
        public List<TabItemTemplates> HelpSectionTabs { get; set; }

        public void UpdateAccountCustomControl(SocialNetworks networks)
        {
            // Accounts tab is at index 1 (Setup=0, Accounts=1, Analytics=2, ...)
            NetworkTabs[1].Content = new Lazy<UserControl>(() => new AccountTab(_strategies));
        }

        public static FvTabHandlerFactory Instance(AccessorStrategies strategies)
        {
            return _instance ?? (_instance = new FvTabHandlerFactory(strategies));
        }

        private void TabInitializer(AccessorStrategies strategies)
        {
            try
            {
                NetworkTabs = new List<TabItemTemplates>
                {
                    // Setup Tab - First tab for API configuration and OAuth connection
                    new TabItemTemplates
                    {
                        Title = "Setup",
                        Content = new Lazy<UserControl>(SetupTab.GetSingletonSetupTab)
                    },

                    // Accounts Tab - Main tab for managing Fanvue accounts
                    new TabItemTemplates
                    {
                        Title = Application.Current.FindResource("LangKeyAccounts")?.ToString() ?? "Accounts",
                        Content = new Lazy<UserControl>(() => new AccountTab(strategies))
                    },

                    // Analytics Tab - Dashboard with charts and combined metrics
                    new TabItemTemplates
                    {
                        Title = "Analytics",
                        Content = new Lazy<UserControl>(() =>
                        {
                            try { return new AnalyticsTab(); }
                            catch (Exception ex)
                            {
                                GlobusLogHelper.log.Error("[FanvueAnalytics][CTOR-DIAG] AnalyticsTab ctor failed: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.ToString());
                                throw;
                            }
                        })
                    },

                    // Engage Tab - Template for engagement features
                    new TabItemTemplates
                    {
                        Title = Application.Current.FindResource("LangKeyEngage")?.ToString() ?? "Engage",
                        Content = new Lazy<UserControl>(EngageTab.GetSingletonObject)
                    },

                    // Scraper Tab - Template for scraping features
                    new TabItemTemplates
                    {
                        Title = Application.Current.FindResource("LangKeyScraper")?.ToString() ?? "Scraper",
                        Content = new Lazy<UserControl>(() => new ScraperTab())
                    },

                    // Settings Tab - Module settings
                    new TabItemTemplates
                    {
                        Title = Application.Current.FindResource("LangKeySettings")?.ToString() ?? "Settings",
                        Content = new Lazy<UserControl>(() => new SettingsTab())
                    }
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FvTabHandlerFactory.TabInitializer] Error: {ex.Message}");
            }
        }
    }
}
