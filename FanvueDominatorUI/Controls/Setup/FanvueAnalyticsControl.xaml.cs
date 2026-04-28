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
    /// Clean analytics dashboard for viewing Fanvue account statistics.
    /// </summary>
    public partial class FanvueAnalyticsControl : UserControl
    {
        private const string LogTag = "[FanvueAnalytics]";

        private FanvueCredentials _credentials;
        private FanvueAuthService _authService;
        private FanvueApiClient _apiClient;
        private CancellationTokenSource _cancellationTokenSource;

        // Cached counts from /users/me endpoint
        private int _cachedFollowerCount = 0;
        private int _cachedSubscriberCount = 0;

        public FanvueAnalyticsControl()
        {
            InitializeComponent();
            LogInfo("Initializing FanvueAnalyticsControl");

            _authService = new FanvueAuthService();
            _authService.CredentialsUpdated += OnCredentialsUpdated;
            _apiClient = new FanvueApiClient(_authService);

            LoadCredentials();
            UpdateConnectionStatus();
            LogInfo("FanvueAnalyticsControl initialized");
        }

        private void OnCredentialsUpdated(object sender, CredentialsUpdatedEventArgs e)
        {
            LogInfo("OnCredentialsUpdated - Saving credentials after token refresh");
            Dispatcher.Invoke(() =>
            {
                SaveCredentials();
            });
        }

        private void SaveCredentials()
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
                var json = JsonConvert.SerializeObject(_credentials, Formatting.Indented);
                System.IO.File.WriteAllText(credentialsPath, json);
                LogDebug("SaveCredentials - Credentials saved successfully");
            }
            catch (Exception ex)
            {
                LogError("SaveCredentials - Failed to save credentials", ex);
            }
        }

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

        #region Connection Status

        private void LoadCredentials()
        {
            LogDebug("LoadCredentials - Loading saved credentials");
            try
            {
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var credentialsPath = System.IO.Path.Combine(appDataPath, "Socinator1.0", "FanvueCredentials.json");

                if (System.IO.File.Exists(credentialsPath))
                {
                    var json = System.IO.File.ReadAllText(credentialsPath);
                    _credentials = JsonConvert.DeserializeObject<FanvueCredentials>(json);
                    if (_credentials != null)
                    {
                        _apiClient.Credentials = _credentials;
                        LogInfo("LoadCredentials - Credentials loaded. IsConnected=" + _credentials.IsConnected);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("LoadCredentials - Failed to load credentials", ex);
                _credentials = null;
            }
        }

        private void UpdateConnectionStatus()
        {
            if (_credentials == null || !_credentials.IsConnected)
            {
                ConnectionIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF4948"));
                TxtConnectionStatus.Text = "Not connected";
                TxtAccountName.Text = "Analytics Dashboard";
                BtnRefreshAll.IsEnabled = false;
            }
            else if (_credentials.IsTokenExpired && !_credentials.CanRefresh)
            {
                ConnectionIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFC107"));
                TxtConnectionStatus.Text = "Token expired - please reconnect";
                TxtAccountName.Text = "Analytics Dashboard";
                BtnRefreshAll.IsEnabled = false;
            }
            else
            {
                var displayName = !string.IsNullOrEmpty(_credentials.Username)
                    ? "@" + _credentials.Username
                    : "Connected";
                ConnectionIndicator.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"));
                TxtConnectionStatus.Text = displayName;
                TxtAccountName.Text = !string.IsNullOrEmpty(_credentials.Username)
                    ? "@" + _credentials.Username + " Analytics"
                    : "Analytics Dashboard";
                BtnRefreshAll.IsEnabled = true;
            }
        }

        #endregion

        #region Refresh All

        private async void BtnRefreshAll_Click(object sender, RoutedEventArgs e)
        {
            LogInfo("RefreshAll - Starting refresh");
            LoadCredentials();
            UpdateConnectionStatus();

            if (_credentials == null || !_credentials.IsConnected)
            {
                return;
            }

            // Show loading state
            BtnRefreshAll.IsEnabled = false;
            LoadingOverlay.Visibility = Visibility.Visible;

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();

                TxtLoadingStatus.Text = "Fetching profile...";
                await FetchProfileData(_cancellationTokenSource.Token);

                TxtLoadingStatus.Text = "Fetching followers...";
                await FetchFollowersData(_cancellationTokenSource.Token);

                TxtLoadingStatus.Text = "Fetching subscribers...";
                await FetchSubscribersData(_cancellationTokenSource.Token);

                TxtLoadingStatus.Text = "Fetching earnings...";
                await FetchEarningsData(_cancellationTokenSource.Token);

                TxtLoadingStatus.Text = "Fetching messages...";
                await FetchChatsData(_cancellationTokenSource.Token);

                TxtLoadingStatus.Text = "Fetching vault folders...";
                await FetchVaultFolders(_cancellationTokenSource.Token);

                // Update last refreshed time
                TxtLastUpdated.Text = "Updated " + DateTime.Now.ToString("h:mm tt");

                LogInfo("RefreshAll - Completed successfully");
            }
            catch (OperationCanceledException)
            {
                LogWarn("RefreshAll - Cancelled");
            }
            catch (Exception ex)
            {
                LogError("RefreshAll - Failed", ex);
            }
            finally
            {
                BtnRefreshAll.IsEnabled = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                _cancellationTokenSource = null;
            }
        }

        #endregion

        #region Profile

        private async Task FetchProfileData(CancellationToken cancellationToken)
        {
            var response = await _apiClient.GetCurrentUserAsync(cancellationToken);

            Dispatcher.Invoke(() =>
            {
                if (response.IsSuccess && response.Data != null)
                {
                    var data = response.Data;

                    // Extract profile info
                    var username = GetJsonValue(data, "username", "handle") ?? "--";
                    var displayName = GetJsonValue(data, "displayName", "name") ?? "--";
                    var email = GetJsonValue(data, "email") ?? "--";
                    var isCreator = data["isCreator"]?.Value<bool>() ?? false;

                    TxtUsername.Text = "@" + username;
                    TxtDisplayName.Text = displayName;
                    TxtEmail.Text = email;
                    TxtAccountType.Text = isCreator ? "Creator" : "Fan";

                    // Update header
                    TxtAccountName.Text = "@" + username + " Analytics";
                    ProfileInfoBorder.Visibility = Visibility.Visible;

                    // Extract fanCounts
                    var fanCounts = data["fanCounts"] as JObject;
                    if (fanCounts != null)
                    {
                        _cachedFollowerCount = fanCounts["followersCount"]?.Value<int>() ?? 0;
                        _cachedSubscriberCount = fanCounts["subscribersCount"]?.Value<int>() ?? 0;
                    }

                    // Update stat cards
                    TxtFollowerCount.Text = FormatCount(_cachedFollowerCount);
                    TxtSubscriberCount.Text = FormatCount(_cachedSubscriberCount);

                    // Extract media counts (vault stats) - nested in contentCounts object
                    var contentCounts = data["contentCounts"] as JObject;
                    if (contentCounts != null)
                    {
                        var imageCount = contentCounts["imageCount"]?.Value<int>() ?? 0;
                        var videoCount = contentCounts["videoCount"]?.Value<int>() ?? 0;
                        var audioCount = contentCounts["audioCount"]?.Value<int>() ?? 0;
                        var totalMedia = imageCount + videoCount + audioCount;

                        TxtImageCount.Text = FormatCount(imageCount);
                        TxtVideoCount.Text = FormatCount(videoCount);
                        TxtAudioCount.Text = FormatCount(audioCount);
                        TxtTotalMedia.Text = totalMedia + " total items";
                        VaultStatsPanel.Visibility = Visibility.Visible;
                    }
                }
            });
        }

        #endregion

        #region Followers

        private async Task FetchFollowersData(CancellationToken cancellationToken)
        {
            var response = await _apiClient.GetFollowersAsync(cancellationToken, 20);

            Dispatcher.Invoke(() =>
            {
                if (response.IsSuccess && response.Data != null)
                {
                    var data = response.Data;
                    var followers = new List<FollowerItem>();
                    var items = data["data"] as JArray;

                    if (items != null && items.Count > 0)
                    {
                        foreach (var item in items)
                        {
                            var jobj = item as JObject;
                            var username = GetJsonValue(jobj, "handle", "username") ?? "Unknown";
                            var registeredAt = GetJsonValue(jobj, "registeredAt", "createdAt") ?? "";
                            followers.Add(new FollowerItem
                            {
                                Username = "@" + username,
                                FollowedAt = FormatDate(registeredAt)
                            });
                        }

                        FollowersList.ItemsSource = followers;
                        TxtNoFollowers.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        FollowersList.ItemsSource = null;
                        TxtNoFollowers.Visibility = Visibility.Visible;
                    }
                }
            });
        }

        #endregion

        #region Subscribers

        private async Task FetchSubscribersData(CancellationToken cancellationToken)
        {
            var response = await _apiClient.GetSubscribersAsync(cancellationToken, 20);

            Dispatcher.Invoke(() =>
            {
                if (response.IsSuccess && response.Data != null)
                {
                    var data = response.Data;
                    var subscribers = new List<SubscriberItem>();
                    var items = data["data"] as JArray;

                    if (items != null && items.Count > 0)
                    {
                        foreach (var item in items)
                        {
                            var jobj = item as JObject;
                            var username = GetJsonValue(jobj, "handle", "username") ?? "Unknown";
                            var subscribedAt = GetJsonValue(jobj, "registeredAt", "subscribedAt") ?? "";
                            subscribers.Add(new SubscriberItem
                            {
                                Username = "@" + username,
                                SubscribedAt = FormatDate(subscribedAt)
                            });
                        }

                        SubscribersList.ItemsSource = subscribers;
                        TxtNoSubscribers.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        SubscribersList.ItemsSource = null;
                        TxtNoSubscribers.Visibility = Visibility.Visible;
                    }
                }
            });
        }

        #endregion

        #region Earnings

        private async Task FetchEarningsData(CancellationToken cancellationToken)
        {
            decimal totalNetCents = 0;
            decimal totalGrossCents = 0;
            decimal thisMonthNetCents = 0;
            decimal thisMonthGrossCents = 0;
            var now = DateTime.UtcNow;
            var firstOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            int pageCount = 0;
            const int maxPages = 20;

            string cursor = null;

            do
            {
                pageCount++;
                var response = await _apiClient.GetEarningsAsync(cancellationToken, null, null, 50, cursor);

                if (!response.IsSuccess || response.Data == null)
                {
                    break;
                }

                var items = response.Data["data"] as JArray;
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        var jobj = item as JObject;
                        if (jobj == null) continue;

                        var netCents = jobj["net"]?.Value<decimal>() ?? 0;
                        var grossCents = jobj["gross"]?.Value<decimal>() ?? 0;
                        var dateStr = jobj["date"]?.ToString();

                        totalNetCents += netCents;
                        totalGrossCents += grossCents;

                        if (!string.IsNullOrEmpty(dateStr) && DateTime.TryParse(dateStr, out var txDate))
                        {
                            if (txDate >= firstOfMonth)
                            {
                                thisMonthNetCents += netCents;
                                thisMonthGrossCents += grossCents;
                            }
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

                if (string.IsNullOrEmpty(cursor) || pageCount >= maxPages)
                {
                    break;
                }

            } while (true);

            // Convert cents to dollars
            var grossDollars = totalGrossCents / 100m;
            var netDollars = totalNetCents / 100m;
            var feesDollars = grossDollars - netDollars;
            var monthGrossDollars = thisMonthGrossCents / 100m;

            Dispatcher.Invoke(() =>
            {
                // Main stat cards show gross (total revenue)
                TxtTotalEarnings.Text = FormatMoney(grossDollars);
                TxtMonthEarnings.Text = FormatMoney(monthGrossDollars);

                // Breakdown section
                TxtGrossEarnings.Text = FormatMoney(grossDollars);
                TxtNetEarnings.Text = FormatMoney(netDollars);
                TxtPlatformFees.Text = FormatMoney(feesDollars);
            });
        }

        #endregion

        #region Chats

        private async Task FetchChatsData(CancellationToken cancellationToken)
        {
            var response = await _apiClient.GetChatsAsync(cancellationToken, 15);

            Dispatcher.Invoke(() =>
            {
                if (response.IsSuccess && response.Data != null)
                {
                    var data = response.Data;
                    var chats = new List<ChatItem>();
                    var items = data["data"] as JArray;

                    if (items != null && items.Count > 0)
                    {
                        TxtChatCount.Text = items.Count + " conversations";

                        foreach (var item in items)
                        {
                            var jobj = item as JObject;
                            var user = jobj?["user"] as JObject;
                            var username = "Unknown";
                            var displayName = "";

                            if (user != null)
                            {
                                username = GetJsonValue(user, "handle", "username") ?? "Unknown";
                                displayName = GetJsonValue(user, "displayName", "nickname") ?? username;
                            }

                            // lastMessage is an OBJECT with a text property, not a string
                            var lastMessageObj = jobj?["lastMessage"] as JObject;
                            var lastMessage = "";
                            if (lastMessageObj != null)
                            {
                                lastMessage = GetJsonValue(lastMessageObj, "text") ?? "";
                            }
                            if (lastMessage.Length > 60)
                            {
                                lastMessage = lastMessage.Substring(0, 57) + "...";
                            }

                            var updatedAt = GetJsonValue(jobj, "lastMessageAt", "updatedAt") ?? "";

                            chats.Add(new ChatItem
                            {
                                Username = username,
                                Initial = username.Length > 0 ? username[0].ToString().ToUpper() : "?",
                                LastMessage = string.IsNullOrEmpty(lastMessage) ? "No messages" : lastMessage,
                                Time = FormatDate(updatedAt)
                            });
                        }

                        ChatsList.ItemsSource = chats;
                        TxtNoMessages.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        ChatsList.ItemsSource = null;
                        TxtChatCount.Text = "";
                        TxtNoMessages.Visibility = Visibility.Visible;
                    }
                }
            });
        }

        #endregion

        #region Vault Folders

        private async Task FetchVaultFolders(CancellationToken cancellationToken)
        {
            try
            {
                var response = await _apiClient.GetVaultFoldersAsync(cancellationToken, 50);

                Dispatcher.Invoke(() =>
                {
                    if (response.IsSuccess && response.Data != null)
                    {
                        var folders = new List<FolderItem>();
                        var items = response.Data["data"] as JArray;

                        if (items != null && items.Count > 0)
                        {
                            foreach (var item in items)
                            {
                                var jobj = item as JObject;
                                var name = GetJsonValue(jobj, "name") ?? "Unnamed";
                                var mediaCount = jobj?["mediaCount"]?.Value<int>() ?? 0;

                                folders.Add(new FolderItem
                                {
                                    Name = name,
                                    MediaCount = mediaCount.ToString()
                                });
                            }

                            FoldersList.ItemsSource = folders;
                            TxtNoFolders.Visibility = Visibility.Collapsed;
                        }
                        else
                        {
                            FoldersList.ItemsSource = null;
                            TxtNoFolders.Visibility = Visibility.Visible;
                        }
                    }
                    else
                    {
                        LogWarn("FetchVaultFolders - API call failed: " + (response.ErrorMessage ?? "Unknown error"));
                        TxtNoFolders.Visibility = Visibility.Visible;
                    }
                });
            }
            catch (Exception ex)
            {
                LogError("FetchVaultFolders - Failed", ex);
            }
        }

        #endregion

        #region Helpers

        private string GetJsonValue(JObject obj, params string[] keys)
        {
            if (obj == null) return null;

            foreach (var key in keys)
            {
                var value = obj[key];
                if (value != null && value.Type != JTokenType.Null)
                {
                    return value.ToString();
                }
            }
            return null;
        }

        private string FormatDate(string dateStr)
        {
            if (string.IsNullOrEmpty(dateStr)) return "";

            if (DateTime.TryParse(dateStr, out var date))
            {
                var diff = DateTime.Now - date;
                if (diff.TotalMinutes < 1) return "now";
                if (diff.TotalMinutes < 60) return string.Format("{0}m", (int)diff.TotalMinutes);
                if (diff.TotalHours < 24) return string.Format("{0}h", (int)diff.TotalHours);
                if (diff.TotalDays < 7) return string.Format("{0}d", (int)diff.TotalDays);
                if (diff.TotalDays < 365) return date.ToString("MMM d");
                return date.ToString("MMM d, yyyy");
            }
            return "";
        }

        private string FormatCount(int count)
        {
            if (count >= 1000000) return string.Format("{0:0.#}M", count / 1000000.0);
            if (count >= 1000) return string.Format("{0:0.#}k", count / 1000.0);
            return count.ToString();
        }

        private string FormatMoney(decimal amount)
        {
            if (amount >= 1000000) return string.Format("${0:0.#}M", amount / 1000000m);
            if (amount >= 1000) return string.Format("${0:0.#}k", amount / 1000m);
            return string.Format("${0:N0}", amount);
        }

        #endregion

        #region Data Classes

        public class FollowerItem
        {
            public string Username { get; set; }
            public string FollowedAt { get; set; }
        }

        public class SubscriberItem
        {
            public string Username { get; set; }
            public string SubscribedAt { get; set; }
        }

        public class ChatItem
        {
            public string Username { get; set; }
            public string Initial { get; set; }
            public string LastMessage { get; set; }
            public string Time { get; set; }
        }

        public class FolderItem
        {
            public string Name { get; set; }
            public string MediaCount { get; set; }
        }

        #endregion
    }
}
