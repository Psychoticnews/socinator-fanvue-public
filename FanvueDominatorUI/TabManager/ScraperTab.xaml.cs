using DominatorHouseCore.Models;
using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace FanvueDominatorUI.TabManager
{
    /// <summary>
    /// Scraper tab for Fanvue module.
    /// Template tab for scraping features like user scraper, content scraper, etc.
    /// </summary>
    public partial class ScraperTab : UserControl
    {
        public ScraperTab()
        {
            InitializeComponent();

            var items = new List<TabItemTemplates>
            {
                // TODO: Add scraper sub-tabs as features are implemented
                // Example:
                // new TabItemTemplates
                // {
                //     Title = "User Scraper",
                //     Content = new Lazy<UserControl>(() => new UserScraperView())
                // },
                new TabItemTemplates
                {
                    Title = "Placeholder",
                    Content = new Lazy<UserControl>(() => new PlaceholderView("Scraper features coming soon..."))
                }
            };
            ScraperTabs.ItemsSource = items;
        }
    }
}
