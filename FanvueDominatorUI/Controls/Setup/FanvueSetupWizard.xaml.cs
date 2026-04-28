using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DominatorHouseCore.Enums;
using DominatorHouseCore.FileManagers;
using DominatorHouseCore.LogHelper;
using DominatorHouseCore.Models;
using DominatorHouseCore.Utility;
using DominatorUIUtility.ViewModel;
using FanvueDominatorCore.Models;
using FanvueDominatorCore.Services;
using Newtonsoft.Json;

namespace FanvueDominatorUI.Controls.Setup
{
    /// <summary>
    /// Setup wizard for connecting multiple Fanvue accounts.
    /// Supports adding, removing, and managing OAuth connections.
    /// </summary>
    public partial class FanvueSetupWizard : UserControl
    {
        private const string DeveloperPortalUrl = "https://www.fanvue.com/developers/apps";
        private const string CredentialsFileName = "FanvueOAuthConfig.json";

        private FanvueAuthService _authService;
        private CancellationTokenSource _cancellationTokenSource;
        private ObservableCollection<FanvueCredentials> _connectedAccounts;
        private bool _isAuthInProgress;
        private const string DisabledTooltip = "Disabled while connecting. Click Cancel to abort.";

        // Shared OAuth config (Client ID/Secret are the same for all accounts)
        private string _savedClientId = string.Empty;
        private string _savedClientSecret = string.Empty;

        public FanvueSetupWizard()
        {
            InitializeComponent();

            _authService = new FanvueAuthService();
            _authService.AuthStatusChanged += OnAuthStatusChanged;
            _connectedAccounts = new ObservableCollection<FanvueCredentials>();

            AccountsList.ItemsSource = _connectedAccounts;

            LoadSavedConfig();
            RefreshAccountsList();
        }

        #region Account Management

        /// <summary>
        /// Refreshes the list of connected accounts from Socinator's account collection.
        /// </summary>
        private void RefreshAccountsList()
        {
            try
            {
                _connectedAccounts.Clear();

                var accountViewModel = InstanceProvider.GetInstance<IDominatorAccountViewModel>();
                if (accountViewModel == null)
                {
                    GlobusLogHelper.log.Warn("[FanvueSetupWizard] Could not get IDominatorAccountViewModel");
                    UpdateAccountsVisibility();
                    return;
                }

                // Get all Fanvue accounts
                var fanvueAccounts = accountViewModel.LstDominatorAccountModel
                    .Where(x => x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue)
                    .ToList();

                foreach (var account in fanvueAccounts)
                {
                    var credentials = GetCredentialsFromAccount(account);
                    if (credentials != null && credentials.IsConnected)
                    {
                        _connectedAccounts.Add(credentials);
                    }
                }

                GlobusLogHelper.log.Info("[FanvueSetupWizard] Loaded " + _connectedAccounts.Count + " connected accounts");
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[FanvueSetupWizard] RefreshAccountsList error: " + ex.Message);
            }

            UpdateAccountsVisibility();
        }

        private void UpdateAccountsVisibility()
        {
            TxtNoAccounts.Visibility = _connectedAccounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Persists in-memory mutations of a DominatorAccountModel to AccountDetails.bin.
        // Without this, ModulePrivateDetails / IsUserLoggedIn / Status changes are lost on restart
        // and the user sees "Failed" + has to re-OAuth (root cause: live-log evidence 2026-04-27 18:45/18:53).
        // Mirrors OutlookEmailScanService.cs:206 + EnableImapHubTab.xaml.cs:1598 pattern.
        private static void PersistAccountToBin(DominatorAccountModel account)
        {
            try
            {
                var fileManager = InstanceProvider.GetInstance<IAccountsFileManager>();
                if (fileManager == null)
                {
                    GlobusLogHelper.log.Warn("[FanvueSetupWizard] PersistAccountToBin: IAccountsFileManager not available");
                    return;
                }
                fileManager.Edit(account);
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[FanvueSetupWizard] PersistAccountToBin error: " + ex.Message);
            }
        }

        private FanvueCredentials GetCredentialsFromAccount(DominatorAccountModel account)
        {
            try
            {
                if (!string.IsNullOrEmpty(account.ModulePrivateDetails))
                {
                    return JsonConvert.DeserializeObject<FanvueCredentials>(account.ModulePrivateDetails);
                }
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[FanvueSetupWizard] GetCredentialsFromAccount error: " + ex.Message);
            }
            return null;
        }

        #endregion

        #region Add Account

        private async void BtnAddAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate credentials
                var clientId = TxtClientId.Text.Trim();
                var clientSecret = ChkShowSecret.IsChecked == true
                    ? TxtClientSecretVisible.Text.Trim()
                    : TxtClientSecret.Password.Trim();

                if (string.IsNullOrEmpty(clientId))
                {
                    MessageBox.Show("Please enter your Client ID.", "Missing Client ID",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrEmpty(clientSecret))
                {
                    MessageBox.Show("Please enter your Client Secret.", "Missing Client Secret",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Save config for reuse
                _savedClientId = clientId;
                _savedClientSecret = clientSecret;
                SaveConfig();

                // Create new credentials for this account
                var credentials = new FanvueCredentials
                {
                    ClientId = clientId,
                    ClientSecret = clientSecret
                };

                UpdateStatus("Opening browser for authorization...", AuthStage.OpeningBrowser);

                _cancellationTokenSource = new CancellationTokenSource();

                // Start OAuth flow
                var success = await _authService.AuthorizeAsync(credentials, _cancellationTokenSource.Token);

                if (success)
                {
                    // Check if this account already exists
                    var existingAccount = _connectedAccounts.FirstOrDefault(x => x.Username == credentials.Username);
                    if (existingAccount != null)
                    {
                        MessageBox.Show("This account (@" + credentials.Username + ") is already connected.",
                            "Account Exists", MessageBoxButton.OK, MessageBoxImage.Information);
                        UpdateStatus("Account already connected.", AuthStage.Idle);
                    }
                    else
                    {
                        // Add account to Socinator's collection. The credential stamp is awaited so
                        // the account row exists and is fully populated BEFORE we say "Successfully
                        // added" and refresh the list — no more 1s blank-row gap.
                        UpdateStatus("Adding @" + credentials.Username + "...", AuthStage.SavingCredentials);
                        var added = await AddAccountToCollectionAsync(credentials);

                        if (added)
                        {
                            RefreshAccountsList();
                            UpdateStatus("Successfully added @" + credentials.Username, AuthStage.Success);
                        }
                        else
                        {
                            RefreshAccountsList();
                            UpdateStatus("Added @" + credentials.Username + ", but credential storage timed out. Try refreshing the Accounts tab.", AuthStage.Error);
                        }
                    }
                }
                else
                {
                    UpdateStatus("Authorization failed or was cancelled.", AuthStage.Error);
                }
            }
            catch (OperationCanceledException)
            {
                UpdateStatus("You cancelled the authorization.", AuthStage.Cancelled);
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[FanvueSetupWizard] BtnAddAccount_Click error: " + ex.Message);
                UpdateStatus(CategorizeError(ex, ex.Message), AuthStage.Error);
            }
            finally
            {
                _cancellationTokenSource = null;
            }
        }

        private TextBlock CreateButtonContent(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(15, 0, 15, 0)
            };
        }

        private async Task<bool> AddAccountToCollectionAsync(FanvueCredentials credentials)
        {
            try
            {
                var accountViewModel = InstanceProvider.GetInstance<IDominatorAccountViewModel>();
                if (accountViewModel == null)
                {
                    GlobusLogHelper.log.Warn("[FanvueSetupWizard] Could not get IDominatorAccountViewModel");
                    return false;
                }

                // Check if already exists
                var existingAccount = accountViewModel.LstDominatorAccountModel
                    .FirstOrDefault(x => x.AccountBaseModel.UserName == credentials.Username &&
                                        x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue);

                if (existingAccount != null)
                {
                    // Update existing account's credentials synchronously
                    existingAccount.ModulePrivateDetails = JsonConvert.SerializeObject(credentials);
                    existingAccount.AccountBaseModel.Status = AccountStatus.Success;
                    existingAccount.IsUserLoggedIn = true;
                    PersistAccountToBin(existingAccount);
                    GlobusLogHelper.log.Info("[FanvueSetupWizard] Updated existing account: " + credentials.Username);
                    return true;
                }

                // Create new account
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

                // AddSingleAccountInThread queues the actual insert on a background thread
                // (DominatorAccountViewModel.cs:5228 ThreadFactory.Instance.Start). We watch the
                // observable collection and stamp credentials the instant our account appears.
                var stamped = await StampCredentialsWhenAccountAppearsAsync(accountViewModel, credentials);

                accountViewModel.AddSingleAccountInThread(accountBaseModel);

                var ok = await stamped;
                if (ok)
                {
                    GlobusLogHelper.log.Info("[FanvueSetupWizard] Added new account and stored credentials: " + credentials.Username);
                }
                else
                {
                    GlobusLogHelper.log.Warn("[FanvueSetupWizard] Added new account but credential stamp timed out: " + credentials.Username);
                }
                return ok;
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[FanvueSetupWizard] AddAccountToCollectionAsync error: " + ex.Message);
                return false;
            }
        }

        // Returns a Task<Task<bool>>: the outer Task completes once the watcher is wired up
        // (so the caller can safely call AddSingleAccountInThread without losing the event),
        // and the inner Task<bool> completes when the credential stamp lands or times out.
        private Task<Task<bool>> StampCredentialsWhenAccountAppearsAsync(IDominatorAccountViewModel accountViewModel, FanvueCredentials credentials)
        {
            var setup = new TaskCompletionSource<Task<bool>>();
            var stamp = new TaskCompletionSource<bool>();
            var collection = accountViewModel.LstDominatorAccountModel;

            // Run subscription on the UI thread so CollectionChanged callbacks marshal correctly.
            Dispatcher.Invoke(new Action(() =>
            {
                NotifyCollectionChangedEventHandler handler = null;
                CancellationTokenSource timeoutCts = null;

                Action<bool> finish = success =>
                {
                    if (handler != null)
                    {
                        try { collection.CollectionChanged -= handler; } catch { }
                        handler = null;
                    }
                    if (timeoutCts != null)
                    {
                        try { timeoutCts.Cancel(); timeoutCts.Dispose(); } catch { }
                        timeoutCts = null;
                    }
                    stamp.TrySetResult(success);
                };

                handler = (s, e) =>
                {
                    if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;

                    DominatorAccountModel match = null;
                    foreach (var item in e.NewItems)
                    {
                        var account = item as DominatorAccountModel;
                        if (account == null) continue;
                        if (account.AccountBaseModel != null &&
                            account.AccountBaseModel.UserName == credentials.Username &&
                            account.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue)
                        {
                            match = account;
                            break;
                        }
                    }
                    if (match == null) return;

                    // Stamp on the UI thread (we're already on it via Dispatcher marshalling
                    // when the collection raises the event).
                    Dispatcher.Invoke(new Action(() =>
                    {
                        try
                        {
                            match.ModulePrivateDetails = JsonConvert.SerializeObject(credentials);
                            match.IsUserLoggedIn = true;
                            PersistAccountToBin(match);
                            finish(true);
                        }
                        catch (Exception ex)
                        {
                            GlobusLogHelper.log.Error("[FanvueSetupWizard] Stamp credentials error: " + ex.Message);
                            finish(false);
                        }
                    }));
                };

                collection.CollectionChanged += handler;

                // Timeout guard so a failure inside AddAccount can never hang the wizard forever.
                timeoutCts = new CancellationTokenSource();
                Task.Delay(TimeSpan.FromSeconds(5), timeoutCts.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    Dispatcher.Invoke(new Action(() =>
                    {
                        // In case the account was added before our handler attached (race),
                        // do one last lookup pass before declaring timeout.
                        var latecomer = collection
                            .FirstOrDefault(x => x.AccountBaseModel != null &&
                                                 x.AccountBaseModel.UserName == credentials.Username &&
                                                 x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue);
                        if (latecomer != null)
                        {
                            try
                            {
                                latecomer.ModulePrivateDetails = JsonConvert.SerializeObject(credentials);
                                latecomer.IsUserLoggedIn = true;
                                PersistAccountToBin(latecomer);
                                finish(true);
                                return;
                            }
                            catch (Exception ex)
                            {
                                GlobusLogHelper.log.Error("[FanvueSetupWizard] Late-stamp credentials error: " + ex.Message);
                            }
                        }
                        finish(false);
                    }));
                }, TaskScheduler.Default);

                setup.SetResult(stamp.Task);
            }));

            return setup.Task;
        }

        #endregion

        #region Remove Account

        private void BtnRemoveAccount_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var button = sender as Button;
                var username = button?.Tag as string;

                if (string.IsNullOrEmpty(username))
                    return;

                var result = MessageBox.Show(
                    "Are you sure you want to remove @" + username + "?",
                    "Confirm Remove",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result != MessageBoxResult.Yes)
                    return;

                // Find and remove from Socinator collection
                var accountViewModel = InstanceProvider.GetInstance<IDominatorAccountViewModel>();
                if (accountViewModel != null)
                {
                    var account = accountViewModel.LstDominatorAccountModel
                        .FirstOrDefault(x => x.AccountBaseModel.UserName == username &&
                                            x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue);

                    if (account != null)
                    {
                        // Clear credentials
                        account.ModulePrivateDetails = null;
                        account.AccountBaseModel.Status = AccountStatus.Failed;
                        account.IsUserLoggedIn = false;

                        // Note: We don't fully remove from collection to preserve any campaign references
                        // The account will show as disconnected/failed
                        GlobusLogHelper.log.Info("[FanvueSetupWizard] Disconnected account: " + username);
                    }
                }

                // Refresh list
                RefreshAccountsList();
                UpdateStatus("Removed @" + username, AuthStage.Idle);
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[FanvueSetupWizard] BtnRemoveAccount_Click error: " + ex.Message);
                MessageBox.Show("Error removing account: " + ex.Message, "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Config Storage

        private void LoadSavedConfig()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var configPath = System.IO.Path.Combine(appDataPath, "Socinator1.0", CredentialsFileName);

                if (System.IO.File.Exists(configPath))
                {
                    var json = System.IO.File.ReadAllText(configPath);
                    var config = JsonConvert.DeserializeAnonymousType(json, new { ClientId = "", ClientSecret = "" });

                    if (config != null)
                    {
                        _savedClientId = config.ClientId ?? string.Empty;
                        _savedClientSecret = config.ClientSecret ?? string.Empty;

                        TxtClientId.Text = _savedClientId;
                        TxtClientSecret.Password = _savedClientSecret;
                        TxtClientSecretVisible.Text = _savedClientSecret;
                    }
                }

                // Also try to load from legacy FanvueCredentials.json
                var legacyPath = System.IO.Path.Combine(appDataPath, "Socinator1.0", "FanvueCredentials.json");
                if (System.IO.File.Exists(legacyPath) && string.IsNullOrEmpty(_savedClientId))
                {
                    var json = System.IO.File.ReadAllText(legacyPath);
                    var creds = JsonConvert.DeserializeObject<FanvueCredentials>(json);
                    if (creds != null && !string.IsNullOrEmpty(creds.ClientId))
                    {
                        _savedClientId = creds.ClientId;
                        _savedClientSecret = creds.ClientSecret;

                        TxtClientId.Text = _savedClientId;
                        TxtClientSecret.Password = _savedClientSecret;
                        TxtClientSecretVisible.Text = _savedClientSecret;
                    }
                }
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[FanvueSetupWizard] LoadSavedConfig error: " + ex.Message);
            }
        }

        private void SaveConfig()
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var socinatorPath = System.IO.Path.Combine(appDataPath, "Socinator1.0");

                if (!System.IO.Directory.Exists(socinatorPath))
                {
                    System.IO.Directory.CreateDirectory(socinatorPath);
                }

                var configPath = System.IO.Path.Combine(socinatorPath, CredentialsFileName);
                var config = new { ClientId = _savedClientId, ClientSecret = _savedClientSecret };
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                System.IO.File.WriteAllText(configPath, json);
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[FanvueSetupWizard] SaveConfig error: " + ex.Message);
            }
        }

        #endregion

        #region External Links

        private void BtnOpenPortal_Click(object sender, RoutedEventArgs e)
        {
            OpenUrl(DeveloperPortalUrl);
        }

        private void BtnCopyUri_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtRedirectUri.Text);

                var originalContent = BtnCopyUri.Content;
                BtnCopyUri.Content = "Copied!";
                BtnCopyUri.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E7E34"));

                var timer = new System.Windows.Threading.DispatcherTimer();
                timer.Interval = TimeSpan.FromSeconds(2);
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    BtnCopyUri.Content = originalContent;
                    BtnCopyUri.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not copy to clipboard: " + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open browser: " + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Show/Hide Secret

        private void ChkShowSecret_Changed(object sender, RoutedEventArgs e)
        {
            if (ChkShowSecret.IsChecked == true)
            {
                TxtClientSecretVisible.Text = TxtClientSecret.Password;
                TxtClientSecret.Visibility = Visibility.Collapsed;
                TxtClientSecretVisible.Visibility = Visibility.Visible;
            }
            else
            {
                TxtClientSecret.Password = TxtClientSecretVisible.Text;
                TxtClientSecretVisible.Visibility = Visibility.Collapsed;
                TxtClientSecret.Visibility = Visibility.Visible;
            }
        }

        #endregion

        #region Status Updates

        private void OnAuthStatusChanged(object sender, AuthStatusEventArgs e)
        {
            Dispatcher.Invoke(new Action(() =>
            {
                UpdateStatus(e.Message, e.Stage);
            }));
        }

        private void UpdateStatus(string message, AuthStage stage)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action(() => UpdateStatus(message, stage)));
                return;
            }

            TxtStatusMessage.Text = message;

            string indicatorHex;
            string statusLabel;
            string stepText;
            bool inFlight;

            switch (stage)
            {
                case AuthStage.ValidatingCredentials:
                    indicatorHex = "#FFC107"; statusLabel = "Connecting...";
                    stepText = "Step 1 of 4: Checking credentials";
                    inFlight = true;
                    break;
                case AuthStage.OpeningBrowser:
                    indicatorHex = "#FFC107"; statusLabel = "Connecting...";
                    stepText = "Step 2 of 4: Opening browser";
                    inFlight = true;
                    break;
                case AuthStage.WaitingForAuthorization:
                    indicatorHex = "#FFC107"; statusLabel = "Connecting...";
                    stepText = "Step 2 of 4: Waiting for you to authorize";
                    inFlight = true;
                    break;
                case AuthStage.ExchangingTokens:
                    indicatorHex = "#FFC107"; statusLabel = "Connecting...";
                    stepText = "Step 3 of 4: Saving authorization";
                    inFlight = true;
                    break;
                case AuthStage.SavingCredentials:
                    indicatorHex = "#FFC107"; statusLabel = "Connecting...";
                    stepText = "Step 4 of 4: Saving account";
                    inFlight = true;
                    break;
                case AuthStage.RefreshingToken:
                    indicatorHex = "#FFC107"; statusLabel = "Connecting...";
                    stepText = "Refreshing token";
                    inFlight = true;
                    break;
                case AuthStage.TestingConnection:
                    indicatorHex = "#FFC107"; statusLabel = "Connecting...";
                    stepText = "Testing connection";
                    inFlight = true;
                    break;
                case AuthStage.Success:
                    indicatorHex = "#28A745"; statusLabel = "Success";
                    stepText = "Connected!";
                    inFlight = false;
                    break;
                case AuthStage.Error:
                    indicatorHex = "#DC3545"; statusLabel = "Error";
                    stepText = "Ready";
                    inFlight = false;
                    break;
                case AuthStage.Cancelled:
                    indicatorHex = "#6C757D"; statusLabel = "Cancelled";
                    stepText = "Ready";
                    inFlight = false;
                    break;
                case AuthStage.Idle:
                default:
                    indicatorHex = "#6C757D"; statusLabel = "Ready";
                    stepText = "Ready";
                    inFlight = false;
                    break;
            }

            StatusIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(indicatorHex));
            TxtConnectionStatus.Text = statusLabel;
            TxtStepIndicator.Text = stepText == "Ready" ? " - Manage Multiple Accounts" : " - " + stepText;

            ApplyInProgressVisuals(inFlight, stage);
        }

        private void ApplyInProgressVisuals(bool inFlight, AuthStage stage)
        {
            _isAuthInProgress = inFlight;

            ConnectProgressRing.IsActive = inFlight;
            ConnectProgressRing.Visibility = inFlight ? Visibility.Visible : Visibility.Collapsed;
            BtnCancelConnect.Visibility = inFlight ? Visibility.Visible : Visibility.Collapsed;

            BtnAddAccount.IsEnabled = !inFlight;
            BtnAddAccount.Content = CreateButtonContent(GetAddAccountButtonText(inFlight, stage));

            TxtClientId.IsEnabled = !inFlight;
            TxtClientSecret.IsEnabled = !inFlight;
            TxtClientSecretVisible.IsEnabled = !inFlight;
            ChkShowSecret.IsEnabled = !inFlight;
            TxtRedirectUri.IsEnabled = !inFlight;
            BtnOpenPortal.IsEnabled = !inFlight;
            BtnCopyUri.IsEnabled = !inFlight;

            var tip = inFlight ? DisabledTooltip : null;
            TxtClientId.ToolTip = tip;
            TxtClientSecret.ToolTip = tip;
            TxtClientSecretVisible.ToolTip = tip;
            ChkShowSecret.ToolTip = tip;
            TxtRedirectUri.ToolTip = tip;
            BtnOpenPortal.ToolTip = tip;
            BtnCopyUri.ToolTip = tip;
        }

        private string GetAddAccountButtonText(bool inFlight, AuthStage stage)
        {
            if (!inFlight) return "Add Account";
            switch (stage)
            {
                case AuthStage.ValidatingCredentials: return "Validating...";
                case AuthStage.OpeningBrowser: return "Opening browser...";
                case AuthStage.WaitingForAuthorization: return "Waiting for authorization...";
                case AuthStage.ExchangingTokens:
                case AuthStage.SavingCredentials: return "Saving...";
                default: return "Connecting...";
            }
        }

        private void BtnCancelConnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Cancel();
                    GlobusLogHelper.log.Info("[FanvueSetupWizard] User cancelled OAuth flow");
                }
                ApplyInProgressVisuals(false, AuthStage.Cancelled);
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error("[FanvueSetupWizard] BtnCancelConnect_Click error: " + ex.Message);
            }
        }

        private string CategorizeError(Exception ex, string rawMessage)
        {
            if (ex is OperationCanceledException)
                return "You cancelled the authorization.";
            if (ex is System.Net.WebException || ex.GetType().Name == "HttpRequestException")
                return "Network error - check your internet connection.";
            if (ex is HttpListenerException)
                return "Port 19876 is already in use. Close other Socinator instances and try again.";

            var msg = rawMessage ?? string.Empty;
            if (msg.IndexOf("invalid_client", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Client ID or Client Secret is wrong. Open the developer portal and copy them again.";
            if (msg.IndexOf("invalid_grant", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Authorization expired. Try connecting again.";
            if (msg.IndexOf("redirect_uri_mismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Redirect URI doesn't match. Click 'Copy' next to the Redirect URI above and paste it into your Fanvue app settings.";

            var firstLine = msg.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var line = firstLine.Length > 0 ? firstLine[0] : msg;
            if (line.Length > 200) line = line.Substring(0, 200);
            return line;
        }

        #endregion
    }
}
