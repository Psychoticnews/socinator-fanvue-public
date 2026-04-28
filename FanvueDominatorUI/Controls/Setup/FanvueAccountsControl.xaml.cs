using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DominatorHouseCore.LogHelper;
using FanvueDominatorCore.Models;
using FanvueDominatorCore.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanvueDominatorUI.Controls.Setup
{
    /// <summary>
    /// Displays connected Fanvue accounts with their stats.
    /// Shows followers, subscribers, and revenue (today/week).
    /// </summary>
    public partial class FanvueAccountsControl : UserControl
    {
        private const string LogTag = "[FanvueAccounts]";

        private FanvueAuthService _authService;
        private FanvueApiClient _apiClient;
        private CancellationTokenSource _cancellationTokenSource;
        private List<FanvueAccountItem> _accounts;

        public FanvueAccountsControl()
        {
            InitializeComponent();
            LogInfo("Initializing FanvueAccountsControl");

            _authService = new FanvueAuthService();
            _authService.CredentialsUpdated += OnCredentialsUpdated;
            _apiClient = new FanvueApiClient(_authService);
            _accounts = new List<FanvueAccountItem>();

            LoadAccountsFromStorage();
        }

        #region Account Loading

        private void LoadAccountsFromStorage()
        {
            LogDebug("LoadAccountsFromStorage - Loading saved accounts");
            _accounts.Clear();

            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var credentialsPath = System.IO.Path.Combine(appDataPath, "Socinator1.0", "FanvueCredentials.json");

                if (System.IO.File.Exists(credentialsPath))
                {
                    var json = System.IO.File.ReadAllText(credentialsPath);
                    var credentials = JsonConvert.DeserializeObject<FanvueCredentials>(json);

                    if (credentials != null && credentials.IsConnected)
                    {
                        var account = new FanvueAccountItem
                        {
                            AccountId = credentials.UserUuid ?? "default",
                            Username = !string.IsNullOrEmpty(credentials.Username)
                                ? "@" + credentials.Username
                                : credentials.Email ?? "Connected Account",
                            Credentials = credentials,
                            StatusText = "Connected",
                            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745")),
                            Followers = "-",
                            Subscribers = "-",
                            RevenueToday = "-",
                            RevenueWeek = "-",
                            LastUpdated = "Not refreshed yet",
                            ErrorVisibility = Visibility.Collapsed,
                            ErrorMessage = ""
                        };
                        _accounts.Add(account);
                        LogInfo("LoadAccountsFromStorage - Found connected account: " + account.Username);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("LoadAccountsFromStorage - Failed to load accounts", ex);
            }

            UpdateUI();
        }

        private void UpdateUI()
        {
            TxtAccountCount.Text = string.Format(" ({0})", _accounts.Count);

            if (_accounts.Count == 0)
            {
                NoAccountsPanel.Visibility = Visibility.Visible;
                AccountsList.Visibility = Visibility.Collapsed;
                BtnRefreshAll.IsEnabled = false;
            }
            else
            {
                NoAccountsPanel.Visibility = Visibility.Collapsed;
                AccountsList.Visibility = Visibility.Visible;
                AccountsList.ItemsSource = null;
                AccountsList.ItemsSource = _accounts;
                BtnRefreshAll.IsEnabled = true;
            }
        }

        #endregion

        #region Refresh All Accounts

        private async void BtnRefreshAll_Click(object sender, RoutedEventArgs e)
        {
            LogInfo("RefreshAll - Starting refresh for all accounts");

            if (_accounts.Count == 0)
            {
                LoadAccountsFromStorage();
                if (_accounts.Count == 0)
                {
                    return;
                }
            }

            BtnRefreshAll.IsEnabled = false;
            TxtRefreshButton.Text = "Refreshing...";
            LoadingOverlay.Visibility = Visibility.Visible;

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();

                for (int i = 0; i < _accounts.Count; i++)
                {
                    var account = _accounts[i];
                    TxtLoadingStatus.Text = string.Format("Refreshing {0}...", account.Username);

                    await RefreshAccountStats(account, _cancellationTokenSource.Token);
                }

                TxtLoadingStatus.Text = "Complete!";
                await Task.Delay(500);
            }
            catch (OperationCanceledException)
            {
                LogWarn("RefreshAll - Operation cancelled");
            }
            catch (Exception ex)
            {
                LogError("RefreshAll - Error during refresh", ex);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                BtnRefreshAll.IsEnabled = true;
                TxtRefreshButton.Text = "Refresh All";
                UpdateUI();
            }
        }

        private async Task RefreshAccountStats(FanvueAccountItem account, CancellationToken cancellationToken)
        {
            LogDebug("RefreshAccountStats - Refreshing: " + account.Username);

            try
            {
                // Set API client credentials
                _apiClient.Credentials = account.Credentials;

                // Clear any previous error
                account.ErrorVisibility = Visibility.Collapsed;
                account.ErrorMessage = "";

                // Fetch profile data (followers, subscribers)
                var profileResponse = await _apiClient.GetCurrentUserAsync(cancellationToken);

                if (!profileResponse.IsSuccess)
                {
                    HandleAccountError(account, profileResponse.ErrorMessage);
                    return;
                }

                var data = profileResponse.Data;

                // Extract follower/subscriber counts from nested fanCounts object
                var fanCounts = data["fanCounts"] as JObject;
                if (fanCounts != null)
                {
                    var followers = fanCounts["followersCount"]?.Value<int>() ?? 0;
                    var subscribers = fanCounts["subscribersCount"]?.Value<int>() ?? 0;
                    account.Followers = FormatNumber(followers);
                    account.Subscribers = FormatNumber(subscribers);
                }

                // Fetch earnings for today and this week
                await FetchEarningsForAccount(account, cancellationToken);

                // Update status
                account.StatusText = "Connected";
                account.StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                account.LastUpdated = "Updated " + DateTime.Now.ToString("h:mm tt");

                LogInfo("RefreshAccountStats - Success for: " + account.Username);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogError("RefreshAccountStats - Error for " + account.Username, ex);
                HandleAccountError(account, ex.Message);
            }
        }

        private async Task FetchEarningsForAccount(FanvueAccountItem account, CancellationToken cancellationToken)
        {
            try
            {
                var now = DateTime.UtcNow;
                var todayStart = now.Date;
                var weekStart = now.Date.AddDays(-(int)now.DayOfWeek);

                decimal todayTotal = 0;
                decimal weekTotal = 0;

                // Fetch recent earnings (paginated)
                string cursor = null;
                int pageCount = 0;
                const int maxPages = 10;

                do
                {
                    pageCount++;
                    var response = await _apiClient.GetEarningsAsync(cancellationToken, weekStart, null, 50, cursor);

                    if (!response.IsSuccess)
                    {
                        LogWarn("FetchEarningsForAccount - API call failed on page " + pageCount + ": " + response.ErrorMessage);
                        break;
                    }

                    var transactions = response.Data["data"] as JArray;
                    if (transactions == null || transactions.Count == 0)
                    {
                        break;
                    }

                    foreach (var txn in transactions)
                    {
                        var jobj = txn as JObject;
                        if (jobj == null) continue;

                        var dateStr = jobj["date"]?.ToString();
                        var netCents = jobj["net"]?.Value<decimal>() ?? 0;

                        DateTime txnDate;
                        if (DateTime.TryParse(dateStr, out txnDate))
                        {
                            // Add to week total
                            weekTotal += netCents;

                            // Add to today total if from today
                            if (txnDate.Date == todayStart)
                            {
                                todayTotal += netCents;
                            }
                        }
                    }

                    // Get next cursor
                    var nextCursorToken = response.Data["nextCursor"];
                    if (nextCursorToken == null || nextCursorToken.Type == JTokenType.Null)
                    {
                        cursor = null;
                    }
                    else if (nextCursorToken.Type == JTokenType.Date)
                    {
                        var dt = nextCursorToken.Value<DateTime>();
                        cursor = dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                    }
                    else
                    {
                        cursor = nextCursorToken.ToString();
                    }

                } while (!string.IsNullOrEmpty(cursor) && pageCount < maxPages);

                // Convert from cents to dollars
                account.RevenueToday = FormatCurrency(todayTotal / 100m);
                account.RevenueWeek = FormatCurrency(weekTotal / 100m);
            }
            catch (Exception ex)
            {
                LogError("FetchEarningsForAccount - Error", ex);
                account.RevenueToday = "Error";
                account.RevenueWeek = "Error";
            }
        }

        private void HandleAccountError(FanvueAccountItem account, string errorMessage)
        {
            LogWarn("HandleAccountError - " + account.Username + ": " + errorMessage);

            account.StatusText = "Error";
            account.StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4948"));
            account.ErrorVisibility = Visibility.Visible;

            if (errorMessage.Contains("refresh") || errorMessage.Contains("token") ||
                errorMessage.Contains("reconnect") || errorMessage.Contains("Unauthorized"))
            {
                account.ErrorMessage = "Session expired. Please reconnect your account.";
            }
            else
            {
                account.ErrorMessage = errorMessage;
            }
        }

        #endregion

        #region Reconnect

        private void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            // Navigate to Setup tab for reconnection
            MessageBox.Show(
                "Please go to the Setup tab and click 'Disconnect' then 'Connect' to reconnect your account.",
                "Reconnect Required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        #endregion

        #region Credential Updates

        private void OnCredentialsUpdated(object sender, CredentialsUpdatedEventArgs e)
        {
            LogInfo("OnCredentialsUpdated - Saving credentials after token refresh");
            Dispatcher.Invoke(() =>
            {
                SaveCredentials(e.Credentials);
            });
        }

        private void SaveCredentials(FanvueCredentials credentials)
        {
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var socinatorPath = System.IO.Path.Combine(appDataPath, "Socinator1.0");

                if (!System.IO.Directory.Exists(socinatorPath))
                {
                    System.IO.Directory.CreateDirectory(socinatorPath);
                }

                var credentialsPath = System.IO.Path.Combine(socinatorPath, "FanvueCredentials.json");
                var json = JsonConvert.SerializeObject(credentials, Formatting.Indented);
                System.IO.File.WriteAllText(credentialsPath, json);
                LogDebug("SaveCredentials - Credentials saved successfully");
            }
            catch (Exception ex)
            {
                LogError("SaveCredentials - Failed to save credentials", ex);
            }
        }

        #endregion

        #region Formatting Helpers

        private string FormatNumber(int value)
        {
            if (value >= 1000000)
            {
                return string.Format("{0:0.#}M", value / 1000000.0);
            }
            if (value >= 1000)
            {
                return string.Format("{0:0.#}K", value / 1000.0);
            }
            return value.ToString();
        }

        private string FormatCurrency(decimal value)
        {
            if (value >= 1000)
            {
                return string.Format("${0:0.#}K", value / 1000m);
            }
            return string.Format("${0:0.00}", value);
        }

        #endregion

        #region Logging Helpers

        private void LogInfo(string message)
        {
            GlobusLogHelper.log.Info(LogTag + " " + message);
        }

        private void LogDebug(string message)
        {
            GlobusLogHelper.log.Debug(LogTag + " " + message);
        }

        private void LogWarn(string message)
        {
            GlobusLogHelper.log.Warn(LogTag + " " + message);
        }

        private void LogError(string message, Exception ex = null)
        {
            if (ex != null)
            {
                GlobusLogHelper.log.Error(LogTag + " " + message + " - Exception: " + ex.Message + "\n" + ex.StackTrace);
            }
            else
            {
                GlobusLogHelper.log.Error(LogTag + " " + message);
            }
        }

        #endregion
    }

    /// <summary>
    /// Represents a Fanvue account with its display stats.
    /// </summary>
    public class FanvueAccountItem
    {
        public string AccountId { get; set; }
        public string Username { get; set; }
        public FanvueCredentials Credentials { get; set; }

        public string StatusText { get; set; }
        public Brush StatusColor { get; set; }
        public string LastUpdated { get; set; }

        public string Followers { get; set; }
        public string Subscribers { get; set; }
        public string RevenueToday { get; set; }
        public string RevenueWeek { get; set; }

        public Visibility ErrorVisibility { get; set; }
        public string ErrorMessage { get; set; }
    }
}
