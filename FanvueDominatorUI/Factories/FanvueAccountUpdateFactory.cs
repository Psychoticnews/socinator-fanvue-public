using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DominatorHouseCore.Enums;
using DominatorHouseCore.Enums.DHEnum;
using DominatorHouseCore.Interfaces;
using DominatorHouseCore.LogHelper;
using DominatorHouseCore.Models;
using DominatorHouseCore.ViewModel;
using FanvueDominatorCore.Models;
using FanvueDominatorCore.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanvueDominatorUI.Factories
{
    /// <summary>
    /// Handles account status checking and stats updates for Fanvue accounts.
    /// Fetches followers, subscribers, and revenue data from the Fanvue API.
    /// </summary>
    public class FanvueAccountUpdateFactory : IAccountUpdateFactoryAsync
    {
        private const string LogTag = "[FanvueAccountUpdate]";

        #region Sync Methods (Not Used for API-based accounts)

        public bool CheckStatus(DominatorAccountModel accountModel)
        {
            // Sync method - use async version instead
            return false;
        }

        public bool SolveCaptchaManually(DominatorAccountModel accountModel)
        {
            // Not applicable for API-based auth
            return false;
        }

        public void UpdateDetails(DominatorAccountModel accountModel)
        {
            // Sync method - use async version instead
        }

        public DailyStatisticsViewModel GetDailyGrowth(string accountId, string username, GrowthPeriod period)
        {
            // TODO: Implement if growth tracking is needed
            return null;
        }

        public List<DailyStatisticsViewModel> GetDailyGrowthForAccount(string accountId, GrowthChartPeriod period)
        {
            // TODO: Implement if growth charts are needed
            return new List<DailyStatisticsViewModel>();
        }

        #endregion

        #region Async Methods

        /// <summary>
        /// Check if the Fanvue account is connected and the API is accessible.
        /// </summary>
        public async Task<bool> CheckStatusAsync(DominatorAccountModel accountModel, CancellationToken token)
        {
            LogInfo("CheckStatusAsync - Checking account: " + accountModel.AccountBaseModel?.UserName);

            try
            {
                var credentials = GetCredentialsFromAccount(accountModel);
                if (credentials == null || !credentials.IsConnected)
                {
                    accountModel.AccountBaseModel.Status = AccountStatus.Failed;
                    LogWarn("CheckStatusAsync - No credentials or not connected");
                    return false;
                }

                var authService = new FanvueAuthService();
                var apiClient = new FanvueApiClient(authService);
                apiClient.Credentials = credentials;

                // Test connection by calling /users/me
                var response = await apiClient.GetCurrentUserAsync(token);

                if (response.IsSuccess)
                {
                    accountModel.AccountBaseModel.Status = AccountStatus.Success;
                    accountModel.IsUserLoggedIn = true;

                    // Update credentials if they were refreshed
                    SaveCredentialsToAccount(accountModel, credentials);

                    LogInfo("CheckStatusAsync - Account is working");
                    return true;
                }
                else
                {
                    accountModel.AccountBaseModel.Status = AccountStatus.Failed;
                    accountModel.IsUserLoggedIn = false;
                    LogWarn("CheckStatusAsync - API call failed: " + response.ErrorMessage);
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogError("CheckStatusAsync - Exception", ex);
                accountModel.AccountBaseModel.Status = AccountStatus.Failed;
                return false;
            }
        }

        /// <summary>
        /// Fetch and update account statistics (followers, subscribers, revenue).
        /// </summary>
        public async Task UpdateDetailsAsync(DominatorAccountModel accountModel, CancellationToken token)
        {
            LogInfo("UpdateDetailsAsync - Updating account: " + accountModel.AccountBaseModel?.UserName);

            try
            {
                var credentials = GetCredentialsFromAccount(accountModel);
                if (credentials == null || !credentials.HasValidCredentials)
                {
                    LogWarn("UpdateDetailsAsync - No valid credentials");
                    return;
                }

                var authService = new FanvueAuthService();
                authService.CredentialsUpdated += (sender, e) =>
                {
                    // Save refreshed credentials
                    SaveCredentialsToAccount(accountModel, e.Credentials);
                };

                var apiClient = new FanvueApiClient(authService);
                apiClient.Credentials = credentials;

                // Fetch profile data (followers, subscribers)
                await FetchProfileStats(accountModel, apiClient, token);

                // Fetch earnings data
                await FetchEarningsStats(accountModel, apiClient, token);

                // Save updated credentials
                SaveCredentialsToAccount(accountModel, credentials);

                LogInfo("UpdateDetailsAsync - Stats updated successfully");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogError("UpdateDetailsAsync - Exception", ex);
            }
        }

        #endregion

        #region Stats Fetching

        private async Task FetchProfileStats(DominatorAccountModel accountModel, FanvueApiClient apiClient, CancellationToken token)
        {
            var response = await apiClient.GetCurrentUserAsync(token);

            if (!response.IsSuccess)
            {
                LogWarn("FetchProfileStats - API call failed: " + response.ErrorMessage);
                return;
            }

            var data = response.Data;

            // Extract from nested fanCounts object
            var fanCounts = data["fanCounts"] as JObject;
            if (fanCounts != null)
            {
                var followers = fanCounts["followersCount"]?.Value<int>() ?? 0;
                var subscribers = fanCounts["subscribersCount"]?.Value<int>() ?? 0;

                // DisplayColumnValue1 = Followers
                accountModel.DisplayColumnValue1 = followers;
                // DisplayColumnValue2 = Subscribers
                accountModel.DisplayColumnValue2 = subscribers;

                LogDebug("FetchProfileStats - Followers: " + followers + ", Subscribers: " + subscribers);
            }

            // Update profile info
            var username = data["handle"]?.ToString();
            var displayName = data["displayName"]?.ToString();

            if (!string.IsNullOrEmpty(username))
            {
                accountModel.AccountBaseModel.UserName = username;
            }
            if (!string.IsNullOrEmpty(displayName))
            {
                accountModel.AccountBaseModel.UserFullName = displayName;
            }
        }

        private async Task FetchEarningsStats(DominatorAccountModel accountModel, FanvueApiClient apiClient, CancellationToken token)
        {
            try
            {
                var now = DateTime.UtcNow;
                var todayStart = now.Date;
                var weekStart = now.Date.AddDays(-(int)now.DayOfWeek);

                decimal todayNet = 0;
                decimal weekNet = 0;
                decimal totalNet = 0;

                // Fetch earnings with pagination
                string cursor = null;
                int pageCount = 0;
                const int maxPages = 20;

                do
                {
                    pageCount++;
                    var response = await apiClient.GetEarningsAsync(token, null, null, 50, cursor);

                    if (!response.IsSuccess)
                    {
                        LogWarn("FetchEarningsStats - API call failed on page " + pageCount);
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
                            // Add to total
                            totalNet += netCents;

                            // Add to week total if within this week
                            if (txnDate.Date >= weekStart)
                            {
                                weekNet += netCents;

                                // Add to today total if from today
                                if (txnDate.Date == todayStart)
                                {
                                    todayNet += netCents;
                                }
                            }
                        }
                    }

                    // Get next cursor (handle date parsing)
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

                // Convert cents to dollars and set display values
                // DisplayColumnValue3 = Revenue Today
                accountModel.DisplayColumnValue3 = (int)(todayNet / 100m);
                // DisplayColumnValue4 = Revenue This Week
                accountModel.DisplayColumnValue4 = (int)(weekNet / 100m);
                // DisplayColumnValue5 = Total Revenue
                accountModel.DisplayColumnValue5 = (int)(totalNet / 100m);

                LogDebug("FetchEarningsStats - Today: $" + (todayNet / 100m) + ", Week: $" + (weekNet / 100m) + ", Total: $" + (totalNet / 100m));
            }
            catch (Exception ex)
            {
                LogError("FetchEarningsStats - Exception", ex);
            }
        }

        #endregion

        #region Credential Helpers

        private FanvueCredentials GetCredentialsFromAccount(DominatorAccountModel accountModel)
        {
            try
            {
                // First try ModulePrivateDetails
                if (!string.IsNullOrEmpty(accountModel.ModulePrivateDetails))
                {
                    var credentials = JsonConvert.DeserializeObject<FanvueCredentials>(accountModel.ModulePrivateDetails);
                    if (credentials != null && credentials.HasValidCredentials)
                    {
                        return credentials;
                    }
                }

                // Fallback to shared credentials file
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var credentialsPath = System.IO.Path.Combine(appDataPath, "Socinator1.0", "FanvueCredentials.json");

                if (System.IO.File.Exists(credentialsPath))
                {
                    var json = System.IO.File.ReadAllText(credentialsPath);
                    return JsonConvert.DeserializeObject<FanvueCredentials>(json);
                }
            }
            catch (Exception ex)
            {
                LogError("GetCredentialsFromAccount - Exception", ex);
            }

            return null;
        }

        private void SaveCredentialsToAccount(DominatorAccountModel accountModel, FanvueCredentials credentials)
        {
            try
            {
                accountModel.ModulePrivateDetails = JsonConvert.SerializeObject(credentials);

                // Also save to shared file
                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var socinatorPath = System.IO.Path.Combine(appDataPath, "Socinator1.0");

                if (!System.IO.Directory.Exists(socinatorPath))
                {
                    System.IO.Directory.CreateDirectory(socinatorPath);
                }

                var credentialsPath = System.IO.Path.Combine(socinatorPath, "FanvueCredentials.json");
                var json = JsonConvert.SerializeObject(credentials, Formatting.Indented);
                System.IO.File.WriteAllText(credentialsPath, json);
            }
            catch (Exception ex)
            {
                LogError("SaveCredentialsToAccount - Exception", ex);
            }
        }

        #endregion

        #region Logging

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
                GlobusLogHelper.log.Error(LogTag + " " + message + " - Exception: " + ex.Message);
            }
            else
            {
                GlobusLogHelper.log.Error(LogTag + " " + message);
            }
        }

        #endregion
    }
}
