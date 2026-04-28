using DominatorHouseCore.Enums;
using DominatorHouseCore.LogHelper;
using DominatorHouseCore.Models;
using DominatorHouseCore.Utility;
using DominatorUIUtility.Views;
using DominatorUIUtility.ViewModel;
using FanvueDominatorCore.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace FanvueDominatorUI.TabManager
{
    /// <summary>
    /// Account management tab for Fanvue module.
    /// Displays account manager and related sub-tabs.
    /// </summary>
    public partial class AccountTab : UserControl
    {
        public AccountTab(AccessorStrategies strategies)
        {
            InitializeComponent();

            // Sync any existing connected accounts from FanvueCredentials.json
            SyncExistingConnectedAccounts();

            var items = new List<TabItemTemplates>
            {
                new TabItemTemplates
                {
                    Title = FindResource("LangKeyAccountsManager")?.ToString() ?? "Account Manager",
                    Content = new Lazy<UserControl>(() =>
                        AccountManager.GetSingletonAccountManager("AccountManager", null, SocialNetworks.Fanvue))
                }
            };
            AccountTabs.ItemsSource = items;
        }

        /// <summary>
        /// Syncs existing connected accounts from FanvueCredentials.json to Socinator's account collection.
        /// This handles accounts that were connected before the AddAccountToCollection code was added.
        /// </summary>
        private void SyncExistingConnectedAccounts()
        {
            try
            {
                // Load credentials from file
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var credentialsPath = System.IO.Path.Combine(appDataPath, "Socinator1.0", "FanvueCredentials.json");

                if (!System.IO.File.Exists(credentialsPath))
                {
                    return;
                }

                var json = System.IO.File.ReadAllText(credentialsPath);
                var credentials = JsonConvert.DeserializeObject<FanvueCredentials>(json);

                if (credentials == null || !credentials.IsConnected || string.IsNullOrEmpty(credentials.Username))
                {
                    return;
                }

                // Get account view model
                var accountViewModel = InstanceProvider.GetInstance<IDominatorAccountViewModel>();
                if (accountViewModel == null)
                {
                    GlobusLogHelper.log.Warn("[AccountTab] Could not get IDominatorAccountViewModel");
                    return;
                }

                // Check if account already exists
                var existingAccount = accountViewModel.LstDominatorAccountModel
                    .FirstOrDefault(x => x.AccountBaseModel.UserName == credentials.Username &&
                                        x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue);

                if (existingAccount != null)
                {
                    // Account exists, just update credentials if needed
                    if (string.IsNullOrEmpty(existingAccount.ModulePrivateDetails))
                    {
                        existingAccount.ModulePrivateDetails = json;
                        GlobusLogHelper.log.Info("[AccountTab] Updated credentials for existing account: " + credentials.Username);
                    }
                    return;
                }

                // Add the account to collection
                GlobusLogHelper.log.Info("[AccountTab] Syncing existing connected account: " + credentials.Username);

                var accountBaseModel = new DominatorAccountBaseModel
                {
                    UserName = credentials.Username,
                    Password = "oauth",
                    AccountNetwork = SocialNetworks.Fanvue,
                    Status = AccountStatus.Success,
                    AccountId = Guid.NewGuid().ToString(),
                    UserFullName = credentials.Username,
                    AccountName = string.Empty
                };

                accountViewModel.AddSingleAccountInThread(accountBaseModel);

                // Store credentials after a delay
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    System.Threading.Tasks.Task.Delay(1000).ContinueWith(_ =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                var addedAccount = accountViewModel.LstDominatorAccountModel
                                    .FirstOrDefault(x => x.AccountBaseModel.UserName == credentials.Username &&
                                                        x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue);

                                if (addedAccount != null)
                                {
                                    addedAccount.ModulePrivateDetails = json;
                                    addedAccount.IsUserLoggedIn = true;
                                    GlobusLogHelper.log.Info("[AccountTab] Stored credentials for synced account: " + credentials.Username);
                                }
                            }
                            catch (Exception ex)
                            {
                                GlobusLogHelper.log.Error("[AccountTab] Error storing credentials: " + ex.Message);
                            }
                        });
                    });
                }));

                GlobusLogHelper.log.Info("[AccountTab] Synced account: " + credentials.Username);
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[AccountTab] SyncExistingConnectedAccounts error: " + ex.Message);
            }
        }
    }
}
