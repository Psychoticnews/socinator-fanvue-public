using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using DominatorHouseCore.Enums;
using DominatorHouseCore.FileManagers;
using DominatorHouseCore.LogHelper;
using DominatorHouseCore.Models;
using DominatorHouseCore.Utility;
using DominatorUIUtility.ViewModel;
using FanvueDominatorCore.Models;
using FanvueDominatorCore.Models.Dtos;
using FanvueDominatorCore.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FanvueDominatorUI.TabManager
{
    /// <summary>
    /// Analytics dashboard with account selection, charts, and combined metrics.
    /// </summary>
    public partial class AnalyticsTab : UserControl
    {
        private const string LogTag = "[FanvueAnalytics]";
        private const string AllAccountsKey = "ALL_ACCOUNTS";

        private List<AccountData> _accounts = new List<AccountData>();
        private string _selectedAccountKey = AllAccountsKey;
        private TimeRangeOption _selectedTimeRange;
        private string _currentBucketFormat = "MMM";
        // Tracks the granularity actually requested from the API for the active range ("day" or "week").
        // Drives the bar chart title ("Daily Earnings" / "Weekly Earnings"). Fanvue rejects "month" with 400.
        private string _currentGranularity = "week";
        private CancellationTokenSource _cancellationTokenSource;
        private FanvueAuthService _authService;

        private string _topSpendersSearch = string.Empty;
        // Recent followers panel removed per user request 2026-04-28; field kept as dead-stub
        // only to avoid touching unrelated TextChanged handler signatures. Safe to delete with the handler later.
        private string _recentFollowersSearch = string.Empty;
        private string _pieMode = "BySource"; // "BySource" | "ByType"  (NetFees removed in #O)
        private string _pieValueMode = "Net"; // "Net" | "Gross"  (Net default per #O.2 user feedback)
        private string _chartType = "Area";  // "Bars" | "Line" | "Area"  (default Area per #I)
        private string _bottomList = "TopSpenders"; // "TopSpenders" | "NewestSubscribers"

        // Persisted "subscribers count last time the user viewed this tab", keyed by username.
        // Stored at %LocalAppData%\Socinator1.0\Fanvue\analytics-last-seen.json so it survives app restarts
        // and per-account credential refreshes (which only touch FanvueCredentials.json).
        private Dictionary<string, LastSeenSnapshot> _lastSeenSnapshots = new Dictionary<string, LastSeenSnapshot>();
        private DateTime _tabLoadedAt = DateTime.MinValue;
        private System.Windows.Threading.DispatcherTimer _markSeenTimer;
        private const int MarkSeenDwellSeconds = 60;

        // Daily follower/subscriber history per account. One row per UTC day. Used by Growth Tracker
        // (#B) and the chart-data picker (#C) for "Followers Gained" / "Subscribers Gained" series
        // since the public API has no historical follower endpoint.
        private Dictionary<string, List<HistorySnapshot>> _snapshotHistory = new Dictionary<string, List<HistorySnapshot>>();

        // Active Growth Tracker window in days. -1 sentinel = "All Time".
        private int _growthWindowDays = 30;
        // #V.2 — Single-series state. The 4 chart toggles (Gross/Net/Followers/Subs) are mutex
        // (radio-button behavior); exactly one is active at any time. Default = Earnings (Gross).
        // Replaces the prior multi-series _showEarningsGross/_showEarningsNet/_showFollowersGained
        // /_showSubscribersGained bools and the deleted Split-By-Account toggle.
        // Values: "EarningsGross" | "EarningsNet" | "FollowersGained" | "SubscribersGained".
        private string _activeSeries = "EarningsGross";
        // Per-bucket subscriber rows from the most recent SubscriberInsights call (cached for #C).
        private List<SubscriberEventRowDto> _lastSubscriberInsightRows = new List<SubscriberEventRowDto>();
        private Dictionary<string, decimal> _breakdownBySource = new Dictionary<string, decimal>();
        private Dictionary<string, decimal> _earningsByType = new Dictionary<string, decimal>();
        // Tab-level Net aggregates (parallel to gross). Pie chart Gross/Net sub-toggle (#O.2) reads from
        // these when _pieValueMode == "Net". Aggregated in RefreshData from per-account *.Net dicts.
        private Dictionary<string, decimal> _breakdownBySourceNet = new Dictionary<string, decimal>();
        private Dictionary<string, decimal> _earningsByTypeNet = new Dictionary<string, decimal>();
        // Aggregated per-bucket net earnings (parallel to _monthlyEarnings/gross). Feeds multi-series chart.
        private Dictionary<string, decimal> _monthlyEarningsNet = new Dictionary<string, decimal>();
        // Tab-level bucket-key -> DateTime aggregate (#P). DrawXAxisLabels reads from this so it
        // can compare actual DateTime year/month/quarter instead of parsing label strings.
        private Dictionary<string, DateTime> _bucketKeyToDate = new Dictionary<string, DateTime>();
        // Parallel to _monthlyEarnings.Keys — sorted bucket start dates (the API's `periodStart`
        // per bucket). Drives FindBucketIndex(rowDate) so daily SubscriberInsight rows + daily
        // snapshot diffs find their containing weekly bucket without fragile string-equality (#Q).
        private List<DateTime> _bucketDates = new List<DateTime>();
        // Set true when EarningsSummary fetch fails (all chunks for at least one account, or the single
        // selected account). Drives the chart/pie error-state UI (#U.2) so the user sees a clear "API
        // error" message instead of a half-filled chart with stale source/type breakdowns.
        private bool _lastFetchFailed = false;
        // #V.7 — set by BtnRefresh_Click for the duration of a manual refresh. RefreshData uses it
        // to snapshot the prior good state before clearing dicts; if the fresh fetch fails the prior
        // state is restored so the user keeps seeing their last-known-good numbers.
        private bool _manualRefreshInFlight = false;
        // Aggregated SubscriberInsights rows across the CHART time range (not the Growth Tracker
        // window). Populated in RefreshData by summing each account.SubscriberInsightRows. Drives
        // BuildSubscribersGainedSeries so the chart matches its bucket labels (#L).
        private List<SubscriberEventRowDto> _chartSubscriberInsightRows = new List<SubscriberEventRowDto>();
        // Whether the most recent fetch hit the SubscriberInsights 365-day truncation. Drives the
        // dashed boundary line + subtitle on the chart (#L).
        private bool _chartSubscriberRangeTruncated = false;
        private string _recentSubscribersSearch = string.Empty;
        private string _recentSpendingSearch = string.Empty;
        private string _accountBreakdownSearch = string.Empty;

        private static readonly List<TimeRangeOption> _timeRanges = BuildTimeRanges();

        private static List<TimeRangeOption> BuildTimeRanges()
        {
            // #V.1 — simplified to exactly 5 ranges. Default = "Today" (index 0). Fewer options keep
            // the dropdown actionable; chunked EarningsSummary (#U.1) handles "All Time" cleanly.
            return new List<TimeRangeOption>
            {
                new TimeRangeOption
                {
                    Label = "Today",
                    ComputeRange = now => (new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc), now)
                },
                new TimeRangeOption
                {
                    Label = "Last 7 Days",
                    ComputeRange = now => (now.AddDays(-7), now)
                },
                new TimeRangeOption
                {
                    Label = "This Month",
                    ComputeRange = now => (new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc), now)
                },
                new TimeRangeOption
                {
                    Label = "This Year",
                    ComputeRange = now => (new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc), now)
                },
                new TimeRangeOption
                {
                    // 2020-01-01 floor: Fanvue did not exist much before then. Chunked EarningsSummary
                    // (#U.1) splits into 365-day windows internally. SubscriberInsights is hard-capped
                    // at 365 days regardless of range — chart subtitles flag the truncation.
                    Label = "All Time",
                    ComputeRange = now => (new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), now)
                }
            };
        }

        // Aggregated data
        private int _totalFollowers = 0;
        private int _totalSubscribers = 0;
        private decimal _totalGrossEarnings = 0;
        private decimal _totalNetEarnings = 0;
        private decimal _monthGrossEarnings = 0;
        private decimal _prevMonthGrossEarnings = 0;
        private int _subscriberDelta = 0;
        private Dictionary<string, decimal> _monthlyEarnings = new Dictionary<string, decimal>();

        public AnalyticsTab()
        {
            GlobusLogHelper.log.Debug("[FanvueAnalytics][CTOR-DIAG] step=1 enter ctor");
            GlobusLogHelper.log.Debug("[FanvueAnalytics][CTOR-DIAG] step=2 before InitializeComponent");
            InitializeComponent();
            GlobusLogHelper.log.Debug("[FanvueAnalytics][CTOR-DIAG] step=3 after InitializeComponent");
            _authService = new FanvueAuthService();
            GlobusLogHelper.log.Debug("[FanvueAnalytics][CTOR-DIAG] step=4 after FanvueAuthService ctor");
            _authService.CredentialsUpdated += OnCredentialsUpdated;
            GlobusLogHelper.log.Debug("[FanvueAnalytics][CTOR-DIAG] step=5 after CredentialsUpdated subscribe");

            PopulateTimeRangeSelector();
            GlobusLogHelper.log.Debug("[FanvueAnalytics][CTOR-DIAG] step=6 after PopulateTimeRangeSelector");

            PopulateChartTypeSelector();
            PopulateBottomListSelector();
            PopulateGrowthWindowSelector();

            Loaded += AnalyticsTab_Loaded;
            Unloaded += AnalyticsTab_Unloaded;
            GlobusLogHelper.log.Debug("[FanvueAnalytics][CTOR-DIAG] step=7 ctor complete");
        }

        private void PopulateTimeRangeSelector()
        {
            CmbTimeRange.Items.Clear();
            foreach (var opt in _timeRanges)
            {
                CmbTimeRange.Items.Add(new ComboBoxItem { Content = opt.Label, Tag = opt });
            }
            // #V.1 — Default = "Today" (index 0). Users land on the most-actionable view by default.
            CmbTimeRange.SelectedIndex = 0;
            _selectedTimeRange = _timeRanges[0];
        }

        private void PopulateChartTypeSelector()
        {
            if (ChartTypeSelector == null) { return; }
            ChartTypeSelector.Items.Clear();
            ChartTypeSelector.Items.Add(new ComboBoxItem { Content = "Bars", Tag = "Bars" });
            ChartTypeSelector.Items.Add(new ComboBoxItem { Content = "Line", Tag = "Line" });
            ChartTypeSelector.Items.Add(new ComboBoxItem { Content = "Area", Tag = "Area" });
            ChartTypeSelector.SelectedIndex = 2; // Area default per #I
            _chartType = "Area";
        }

        private void PopulateBottomListSelector()
        {
            if (BottomListSelector == null) { return; }
            BottomListSelector.Items.Clear();
            BottomListSelector.Items.Add(new ComboBoxItem { Content = "Top Spenders", Tag = "TopSpenders" });
            BottomListSelector.Items.Add(new ComboBoxItem { Content = "Newest Subscribers", Tag = "NewestSubscribers" });
            BottomListSelector.SelectedIndex = 0;
            _bottomList = "TopSpenders";
        }

        private void PopulateGrowthWindowSelector()
        {
            if (GrowthWindowSelector == null) { return; }
            GrowthWindowSelector.Items.Clear();
            GrowthWindowSelector.Items.Add(new ComboBoxItem { Content = "Last 7 Days",   Tag = 7 });
            GrowthWindowSelector.Items.Add(new ComboBoxItem { Content = "Last 30 Days",  Tag = 30 });
            GrowthWindowSelector.Items.Add(new ComboBoxItem { Content = "Last 90 Days",  Tag = 90 });
            GrowthWindowSelector.Items.Add(new ComboBoxItem { Content = "Last 6 Months", Tag = 180 });
            GrowthWindowSelector.Items.Add(new ComboBoxItem { Content = "Last 12 Months", Tag = 365 });
            GrowthWindowSelector.Items.Add(new ComboBoxItem { Content = "All Time",      Tag = -1 });
            GrowthWindowSelector.SelectedIndex = 1; // "Last 30 Days"
            _growthWindowDays = 30;
        }

        private void AnalyticsTab_Loaded(object sender, RoutedEventArgs e)
        {
            LoadLastSeenSnapshots();
            LoadSnapshotHistory();
            LoadSectionState();
            ApplyPersistedSectionState();
            // #V.3 — hydrate from analytics-cache.json (if fresh + range-matched) BEFORE LoadAccounts
            // so the user sees data instantly. LoadAccounts() then fires the actual API fetch which
            // replaces the UI when it completes; TxtCacheStatus shows "Loaded from cache · Updating…"
            // during that interval.
            bool hydrated = LoadAnalyticsCache();
            if (hydrated)
            {
                if (TxtCacheStatus != null)
                {
                    TxtCacheStatus.Text = "Loaded from cache · Updating…";
                    TxtCacheStatus.Visibility = Visibility.Visible;
                }
                UpdateUI();
                DrawCharts();
                GlobusLogHelper.log.Debug(LogTag + " Cache hit, hydrated UI, firing background refresh");
            }
            LoadAccounts();
        }

        private void AnalyticsTab_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _authService.CredentialsUpdated -= OnCredentialsUpdated;
            }
            catch { }
            try
            {
                if (_markSeenTimer != null)
                {
                    _markSeenTimer.Stop();
                    _markSeenTimer.Tick -= MarkSeenTimer_Tick;
                    _markSeenTimer = null;
                }
            }
            catch { }
        }

        private void OnCredentialsUpdated(object sender, CredentialsUpdatedEventArgs e)
        {
            try
            {
                if (e == null || e.Credentials == null) { return; }

                var account = _accounts?.FirstOrDefault(a => a.Username == e.Credentials.Username);
                if (account == null)
                {
                    LogWarn("OnCredentialsUpdated: no account match for refreshed credentials");
                    return;
                }

                account.Credentials = e.Credentials;

                if (account.DominatorAccount != null)
                {
                    account.DominatorAccount.ModulePrivateDetails = JsonConvert.SerializeObject(e.Credentials);
                    account.DominatorAccount.IsUserLoggedIn = true;
                    try { InstanceProvider.GetInstance<IAccountsFileManager>()?.Edit(account.DominatorAccount); } catch { }
                }

                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var socinatorPath = System.IO.Path.Combine(appDataPath, "Socinator1.0");
                if (!Directory.Exists(socinatorPath)) { Directory.CreateDirectory(socinatorPath); }
                var credentialsPath = System.IO.Path.Combine(socinatorPath, "FanvueCredentials.json");
                File.WriteAllText(credentialsPath, JsonConvert.SerializeObject(e.Credentials, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LogWarn("OnCredentialsUpdated failed: " + ex.Message);
            }
        }

        #region Account Loading

        private void LoadAccounts()
        {
            try
            {
                _accounts.Clear();
                CmbAccountSelector.Items.Clear();

                var accountViewModel = InstanceProvider.GetInstance<IDominatorAccountViewModel>();
                if (accountViewModel == null)
                {
                    ShowNoAccounts();
                    return;
                }

                var fanvueAccounts = accountViewModel.LstDominatorAccountModel
                    .Where(x => x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue)
                    .ToList();

                foreach (var account in fanvueAccounts)
                {
                    var credentials = GetCredentialsFromAccount(account);
                    if (credentials != null && credentials.IsConnected)
                    {
                        _accounts.Add(new AccountData
                        {
                            Username = credentials.Username,
                            Email = credentials.Email,
                            Credentials = credentials,
                            DominatorAccount = account
                        });
                    }
                }

                if (_accounts.Count == 0)
                {
                    ShowNoAccounts();
                    return;
                }

                NoAccountsPanel.Visibility = Visibility.Collapsed;

                // Add "All Accounts" option
                CmbAccountSelector.Items.Add(new ComboBoxItem
                {
                    Content = "All Accounts (" + _accounts.Count + ")",
                    Tag = AllAccountsKey
                });

                // Add individual accounts
                foreach (var acc in _accounts)
                {
                    CmbAccountSelector.Items.Add(new ComboBoxItem
                    {
                        Content = "@" + acc.Username,
                        Tag = acc.Username
                    });
                }

                CmbAccountSelector.SelectedIndex = 0;
                LogInfo("Loaded " + _accounts.Count + " accounts");
            }
            catch (Exception ex)
            {
                LogError("LoadAccounts failed", ex);
                ShowNoAccounts();
            }
        }

        private void ShowNoAccounts()
        {
            NoAccountsPanel.Visibility = Visibility.Visible;
            BtnRefresh.IsEnabled = false;
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
            catch { }
            return null;
        }

        #endregion

        #region Event Handlers

        private void CmbAccountSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = CmbAccountSelector.SelectedItem as ComboBoxItem;
            if (selected == null) return;

            _selectedAccountKey = selected.Tag as string ?? AllAccountsKey;
            AccountBreakdownPanel.Visibility = _selectedAccountKey == AllAccountsKey ? Visibility.Visible : Visibility.Collapsed;

            // Auto-refresh on selection change
            RefreshData();
        }

        // #V.7 — manual Refresh forces a fresh API fetch and bypasses the cache hydrate path. The
        // cache itself is left in place — only replaced after a SUCCESSFUL fetch — so a failed
        // manual refresh keeps the prior good data visible (snapshot/restore in RefreshData).
        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            GlobusLogHelper.log.Debug(LogTag + " BtnRefresh_Click manual force-refresh; bypassing cache");
            if (TxtCacheStatus != null)
            {
                TxtCacheStatus.Text = "Refreshing…";
                TxtCacheStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFD700"));
                TxtCacheStatus.Visibility = Visibility.Visible;
            }
            _manualRefreshInFlight = true;
            try
            {
                await RefreshData();
            }
            finally
            {
                _manualRefreshInFlight = false;
            }
        }

        // #V.4 — dismiss API error banner. Auto-clears on next successful fetch too.
        private void BtnDismissApiError_Click(object sender, RoutedEventArgs e)
        {
            GlobusLogHelper.log.Debug(LogTag + " Api error banner dismissed");
            HideApiErrorBanner();
        }

        // #V.4 — show / hide API error banner. Centralized so chunked-fetch failures and any future
        // subordinate-call failures share the same UI surface.
        private void ShowApiErrorBanner(string message)
        {
            if (BorderApiErrorBanner == null) { return; }
            string text = string.IsNullOrEmpty(message) ? "API error — couldn't fetch latest data." : message;
            if (TxtApiErrorMessage != null) { TxtApiErrorMessage.Text = text; }
            BorderApiErrorBanner.Visibility = Visibility.Visible;
            GlobusLogHelper.log.Debug(LogTag + " ShowApiErrorBanner: " + text);
        }

        private void HideApiErrorBanner()
        {
            if (BorderApiErrorBanner == null) { return; }
            BorderApiErrorBanner.Visibility = Visibility.Collapsed;
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            GlobusLogHelper.log.Debug(LogTag + " Export started");
            try
            {
                if (_accounts == null || _accounts.Count == 0)
                {
                    FlashStatus("Nothing to export — no accounts loaded.", isError: true);
                    GlobusLogHelper.log.Debug(LogTag + " Export aborted: no accounts");
                    return;
                }

                var dlg = new SaveFileDialog
                {
                    Title = "Export Analytics Summary",
                    DefaultExt = ".csv",
                    Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
                    FileName = "fanvue-analytics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                };
                var ok = dlg.ShowDialog(Window.GetWindow(this));
                if (ok != true)
                {
                    GlobusLogHelper.log.Debug(LogTag + " Export cancelled by user");
                    return;
                }

                var path = dlg.FileName;
                bool isJson = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || dlg.FilterIndex == 2;

                var rangeLabel = _selectedTimeRange != null ? _selectedTimeRange.Label : "(none)";
                var rangeBounds = (_selectedTimeRange ?? _timeRanges[0]).ComputeRange(DateTime.UtcNow);

                if (isJson)
                {
                    File.WriteAllText(path, BuildExportJson(rangeLabel, rangeBounds.start, rangeBounds.end));
                }
                else
                {
                    File.WriteAllText(path, BuildExportCsv(rangeLabel, rangeBounds.start, rangeBounds.end));
                }

                GlobusLogHelper.log.Debug(LogTag + " Export completed: " + path);
                FlashStatus("Exported to " + System.IO.Path.GetFileName(path), isError: false);
            }
            catch (Exception ex)
            {
                LogError("BtnExport_Click failed", ex);
                FlashStatus("Export failed: " + ex.Message, isError: true);
            }
        }

        private void FlashStatus(string message, bool isError)
        {
            if (TxtLastUpdated == null) { return; }
            TxtLastUpdated.Text = message;
            try
            {
                if (isError)
                {
                    TxtLastUpdated.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336"));
                }
                else
                {
                    TxtLastUpdated.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4CAF50"));
                }
            }
            catch { }
            // Auto-revert to the standard "Updated h:mm tt" text after 4s so the status doesn't linger.
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (s, e) =>
            {
                try
                {
                    timer.Stop();
                    if (TxtLastUpdated == null) { return; }
                    TxtLastUpdated.Text = "Updated " + DateTime.Now.ToString("h:mm tt");
                    TxtLastUpdated.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888"));
                }
                catch { }
            };
            timer.Start();
        }

        private IEnumerable<AccountData> GetExportTargets()
        {
            if (_selectedAccountKey == AllAccountsKey) { return _accounts; }
            var single = _accounts.FirstOrDefault(a => a.Username == _selectedAccountKey);
            return single != null ? new[] { single } : new AccountData[0];
        }

        private string BuildExportCsv(string rangeLabel, DateTime rangeStart, DateTime rangeEnd)
        {
            var sb = new StringBuilder();
            var exportedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var startIso = rangeStart.ToString("o", CultureInfo.InvariantCulture);
            var endIso = rangeEnd.ToString("o", CultureInfo.InvariantCulture);

            foreach (var acc in GetExportTargets())
            {
                var uuid = acc.Credentials != null ? (acc.Credentials.UserUuid ?? "") : "";

                sb.AppendLine("# Summary");
                sb.AppendLine("Key,Value");
                AppendKv(sb, "Username", "@" + (acc.Username ?? ""));
                AppendKv(sb, "User UUID", uuid);
                AppendKv(sb, "Exported At (UTC)", exportedAt);
                AppendKv(sb, "Range Label", rangeLabel);
                AppendKv(sb, "Range Start (UTC)", startIso);
                AppendKv(sb, "Range End (UTC)", endIso);
                AppendKv(sb, "Granularity", _currentGranularity ?? "");
                AppendKv(sb, "All-Time Gross", FormatExportMoney(acc.TotalGross));
                AppendKv(sb, "All-Time Net", FormatExportMoney(acc.TotalNet));
                AppendKv(sb, "This Month Gross", FormatExportMoney(acc.MonthGross));
                AppendKv(sb, "This Month Net", FormatExportMoney(acc.MonthNet));
                AppendKv(sb, "Previous Month Gross", FormatExportMoney(acc.PrevMonthGross));
                AppendKv(sb, "Month-over-Month % Change", ComputeMomPercent(acc.MonthGross, acc.PrevMonthGross));
                AppendKv(sb, "Followers (Current)", acc.Followers.ToString(CultureInfo.InvariantCulture));
                AppendKv(sb, "Subscribers (Current)", acc.Subscribers.ToString(CultureInfo.InvariantCulture));
                AppendKv(sb, "New Subscribers In Range", acc.NewSubscribersInRange.ToString(CultureInfo.InvariantCulture));
                AppendKv(sb, "Cancelled Subscribers In Range", acc.CancelledSubscribersInRange.ToString(CultureInfo.InvariantCulture));
                AppendKv(sb, "Net Subscriber Delta In Range", acc.SubscriberDelta.ToString(CultureInfo.InvariantCulture));
                // Messages-sent counter is not exposed by the analytics API at present; the "messages" entry in
                // BreakdownBySource is gross dollars from messaging revenue, not a count. Surface that gross
                // instead so the spec's "if available" line is honored without inventing a metric.
                decimal messagingGross = 0;
                if (acc.BreakdownBySource != null && acc.BreakdownBySource.TryGetValue("messages", out var msgGross)) { messagingGross = msgGross; }
                AppendKv(sb, "Messaging Gross In Range", FormatExportMoney(messagingGross));
                sb.AppendLine();

                sb.AppendLine("# Source Breakdown");
                sb.AppendLine("Source,Gross,Net");
                if (acc.BreakdownBySource != null)
                {
                    foreach (var kv in acc.BreakdownBySource.OrderByDescending(k => k.Value))
                    {
                        decimal net = 0;
                        if (acc.BreakdownBySourceNet != null) { acc.BreakdownBySourceNet.TryGetValue(kv.Key, out net); }
                        sb.Append(CsvEscape(kv.Key)).Append(',').Append(FormatExportMoney(kv.Value)).Append(',').AppendLine(FormatExportMoney(net));
                    }
                }
                sb.AppendLine();

                sb.AppendLine("# Earnings By Type");
                sb.AppendLine("Type,Gross,Net");
                if (acc.EarningsByType != null)
                {
                    foreach (var kv in acc.EarningsByType.OrderByDescending(k => k.Value))
                    {
                        decimal net = 0;
                        if (acc.EarningsByTypeNet != null) { acc.EarningsByTypeNet.TryGetValue(kv.Key, out net); }
                        sb.Append(CsvEscape(kv.Key)).Append(',').Append(FormatExportMoney(kv.Value)).Append(',').AppendLine(FormatExportMoney(net));
                    }
                }
                sb.AppendLine();

                sb.AppendLine("# Per-Bucket Earnings");
                sb.AppendLine("Bucket,Gross,Net");
                if (acc.MonthlyEarnings != null)
                {
                    foreach (var kv in acc.MonthlyEarnings)
                    {
                        decimal net = 0;
                        if (acc.MonthlyEarningsNet != null) { acc.MonthlyEarningsNet.TryGetValue(kv.Key, out net); }
                        sb.Append(CsvEscape(kv.Key)).Append(',').Append(FormatExportMoney(kv.Value)).Append(',').AppendLine(FormatExportMoney(net));
                    }
                }
                sb.AppendLine();
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string BuildExportJson(string rangeLabel, DateTime rangeStart, DateTime rangeEnd)
        {
            var exportedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var accounts = new List<object>();

            foreach (var acc in GetExportTargets())
            {
                var uuid = acc.Credentials != null ? (acc.Credentials.UserUuid ?? "") : "";

                decimal messagingGross = 0;
                if (acc.BreakdownBySource != null && acc.BreakdownBySource.TryGetValue("messages", out var msgGross)) { messagingGross = msgGross; }

                var sources = new List<object>();
                if (acc.BreakdownBySource != null)
                {
                    foreach (var kv in acc.BreakdownBySource.OrderByDescending(k => k.Value))
                    {
                        decimal net = 0;
                        if (acc.BreakdownBySourceNet != null) { acc.BreakdownBySourceNet.TryGetValue(kv.Key, out net); }
                        sources.Add(new { source = kv.Key, gross = kv.Value, net });
                    }
                }
                var types = new List<object>();
                if (acc.EarningsByType != null)
                {
                    foreach (var kv in acc.EarningsByType.OrderByDescending(k => k.Value))
                    {
                        decimal net = 0;
                        if (acc.EarningsByTypeNet != null) { acc.EarningsByTypeNet.TryGetValue(kv.Key, out net); }
                        types.Add(new { type = kv.Key, gross = kv.Value, net });
                    }
                }
                var buckets = new List<object>();
                if (acc.MonthlyEarnings != null)
                {
                    foreach (var kv in acc.MonthlyEarnings)
                    {
                        decimal net = 0;
                        if (acc.MonthlyEarningsNet != null) { acc.MonthlyEarningsNet.TryGetValue(kv.Key, out net); }
                        buckets.Add(new { date = kv.Key, gross = kv.Value, net });
                    }
                }

                accounts.Add(new
                {
                    account = new { username = acc.Username, userUuid = uuid },
                    exportedAt,
                    range = new
                    {
                        label = rangeLabel,
                        start = rangeStart.ToString("o", CultureInfo.InvariantCulture),
                        end = rangeEnd.ToString("o", CultureInfo.InvariantCulture),
                        granularity = _currentGranularity ?? ""
                    },
                    allTime = new { gross = acc.TotalGross, net = acc.TotalNet },
                    thisMonth = new
                    {
                        gross = acc.MonthGross,
                        net = acc.MonthNet,
                        previousMonthGross = acc.PrevMonthGross,
                        momPercentChange = ComputeMomPercentRaw(acc.MonthGross, acc.PrevMonthGross)
                    },
                    subscribers = new
                    {
                        followers = acc.Followers,
                        subscribers = acc.Subscribers,
                        newInRange = acc.NewSubscribersInRange,
                        cancelledInRange = acc.CancelledSubscribersInRange,
                        netDeltaInRange = acc.SubscriberDelta
                    },
                    messagingGrossInRange = messagingGross,
                    sources,
                    types,
                    buckets
                });
            }

            var root = new
            {
                exportedAt,
                rangeLabel,
                accountCount = accounts.Count,
                accounts
            };
            return JsonConvert.SerializeObject(root, Formatting.Indented);
        }

        private static void AppendKv(StringBuilder sb, string key, string value)
        {
            sb.Append(CsvEscape(key)).Append(',').AppendLine(CsvEscape(value));
        }

        private static string CsvEscape(string s)
        {
            if (s == null) { return string.Empty; }
            bool needsQuote = s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            if (!needsQuote) { return s; }
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static string FormatExportMoney(decimal amount)
        {
            // Plain number with 2 decimals, invariant culture — never localised commas/dots in export.
            return amount.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string ComputeMomPercent(decimal current, decimal previous)
        {
            if (previous == 0) { return ""; }
            var pct = ((double)(current - previous) / (double)previous) * 100.0;
            return pct.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static double? ComputeMomPercentRaw(decimal current, decimal previous)
        {
            if (previous == 0) { return null; }
            return ((double)(current - previous) / (double)previous) * 100.0;
        }

        private void ChartTypeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            GlobusLogHelper.log.Debug(LogTag + " ChartTypeSelector changed");
            var selected = ChartTypeSelector.SelectedItem as ComboBoxItem;
            if (selected == null) { return; }
            var tag = selected.Tag as string;
            if (string.IsNullOrEmpty(tag)) { return; }
            _chartType = tag;
            if (!IsLoaded) { return; }
            DrawBarChart();
        }

        private void BottomListSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            GlobusLogHelper.log.Debug(LogTag + " BottomListSelector changed");
            var selected = BottomListSelector.SelectedItem as ComboBoxItem;
            if (selected == null) { return; }
            var tag = selected.Tag as string;
            if (string.IsNullOrEmpty(tag)) { return; }
            _bottomList = tag;
            ApplyBottomListVisibility();
        }

        private async void GrowthWindowSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            GlobusLogHelper.log.Debug(LogTag + " GrowthWindowSelector changed");
            var selected = GrowthWindowSelector.SelectedItem as ComboBoxItem;
            if (selected == null) { return; }
            if (!(selected.Tag is int)) { return; }
            _growthWindowDays = (int)selected.Tag;
            if (!IsLoaded) { return; }
            if (_accounts == null || _accounts.Count == 0) { return; }
            await RefreshGrowthTrackerAsync();
        }

        // Multi-series toggle handler (#C revised). Wired to Checked AND Unchecked on all four
        // series ToggleButtons. Reads the sender's Name to flip the matching bool, then redraws.
        // #V.2 — single-series mutex. Exactly one of the 4 toggles (Gross/Net/Followers/Subs) is
        // checked at any time; user can switch but cannot deselect all.
        private void ChartSeriesToggle_Changed(object sender, RoutedEventArgs e)
        {
            var btn = sender as System.Windows.Controls.Primitives.ToggleButton;
            if (btn == null) { return; }
            bool on = btn.IsChecked == true;
            if (!on)
            {
                // Re-check so user can't deselect all — exactly one must remain active.
                btn.IsChecked = true;
                return;
            }
            GlobusLogHelper.log.Debug(LogTag + " ChartSeriesToggle changed " + btn.Name + "=true (mutex)");

            // Uncheck the others.
            if (btn != ToggleEarningsGross && ToggleEarningsGross != null) ToggleEarningsGross.IsChecked = false;
            if (btn != ToggleEarningsNet && ToggleEarningsNet != null) ToggleEarningsNet.IsChecked = false;
            if (btn != ToggleFollowersGained && ToggleFollowersGained != null) ToggleFollowersGained.IsChecked = false;
            if (btn != ToggleSubscribersGained && ToggleSubscribersGained != null) ToggleSubscribersGained.IsChecked = false;

            switch (btn.Name)
            {
                case "ToggleEarningsGross":     _activeSeries = "EarningsGross"; break;
                case "ToggleEarningsNet":       _activeSeries = "EarningsNet"; break;
                case "ToggleFollowersGained":   _activeSeries = "FollowersGained"; break;
                case "ToggleSubscribersGained": _activeSeries = "SubscribersGained"; break;
            }
            if (!IsLoaded) { return; }
            DrawBarChart();
        }

        // -------------------------------------------------------------------
        // D. Collapsible section handlers + persistence
        // -------------------------------------------------------------------

        // Each *_HeaderClick handler is a single click toggle: flip the persisted state, sync the
        // body Visibility + chevron glyph, save. Default state for any unseen section is expanded.

        private void EarningsSection_HeaderClick(object sender, MouseButtonEventArgs e)
        {
            bool expanded = !GetSectionExpanded("earnings");
            GlobusLogHelper.log.Debug(LogTag + " EarningsSection_HeaderClick expanded=" + expanded);
            _sectionState["earnings"] = expanded;
            ApplyEarningsSectionVisibility();
            SaveSectionState();
        }

        private void RevenueSection_HeaderClick(object sender, MouseButtonEventArgs e)
        {
            bool expanded = !GetSectionExpanded("revenue");
            GlobusLogHelper.log.Debug(LogTag + " RevenueSection_HeaderClick expanded=" + expanded);
            _sectionState["revenue"] = expanded;
            ApplyRevenueSectionVisibility();
            SaveSectionState();
        }

        private void AccountsSection_HeaderClick(object sender, MouseButtonEventArgs e)
        {
            bool expanded = !GetSectionExpanded("accountBreakdown");
            GlobusLogHelper.log.Debug(LogTag + " AccountsSection_HeaderClick expanded=" + expanded);
            _sectionState["accountBreakdown"] = expanded;
            ApplyAccountsSectionVisibility();
            SaveSectionState();
        }

        private void BottomListSection_HeaderClick(object sender, MouseButtonEventArgs e)
        {
            bool expanded = !GetSectionExpanded("bottomList");
            GlobusLogHelper.log.Debug(LogTag + " BottomListSection_HeaderClick expanded=" + expanded);
            _sectionState["bottomList"] = expanded;
            ApplyBottomListSectionVisibility();
            SaveSectionState();
        }

        private bool GetSectionExpanded(string key)
        {
            // Sections default to expanded if there's no persisted entry yet.
            bool v;
            if (_sectionState.TryGetValue(key, out v)) { return v; }
            return true;
        }

        private void ApplyEarningsSectionVisibility()
        {
            bool expanded = GetSectionExpanded("earnings");
            if (EarningsSectionContent != null) { EarningsSectionContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; }
            if (TxtEarningsChevron != null) { TxtEarningsChevron.Text = expanded ? "▼" : "▶"; }
        }
        private void ApplyRevenueSectionVisibility()
        {
            bool expanded = GetSectionExpanded("revenue");
            if (RevenueSectionContent != null) { RevenueSectionContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; }
            if (TxtRevenueChevron != null) { TxtRevenueChevron.Text = expanded ? "▼" : "▶"; }
        }
        private void ApplyAccountsSectionVisibility()
        {
            bool expanded = GetSectionExpanded("accountBreakdown");
            if (AccountBreakdownScroll != null) { AccountBreakdownScroll.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; }
            if (TxtAccountsChevron != null) { TxtAccountsChevron.Text = expanded ? "▼" : "▶"; }
        }
        private void ApplyBottomListSectionVisibility()
        {
            bool expanded = GetSectionExpanded("bottomList");
            if (BottomListSearchRow != null) { BottomListSearchRow.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; }
            if (BottomListContent != null) { BottomListContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; }
            if (TxtBottomListChevron != null) { TxtBottomListChevron.Text = expanded ? "▼" : "▶"; }
        }

        // Persisted to %LocalAppData%\Socinator1.0\Fanvue\analytics-section-state.json so the user's
        // collapsed/expanded choice survives tab reload and app restart.
        private Dictionary<string, bool> _sectionState = new Dictionary<string, bool>();

        private string GetSectionStatePath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = System.IO.Path.Combine(appDataPath, "Socinator1.0", "Fanvue");
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            return System.IO.Path.Combine(dir, "analytics-section-state.json");
        }

        private void LoadSectionState()
        {
            try
            {
                var path = GetSectionStatePath();
                if (!File.Exists(path))
                {
                    _sectionState = new Dictionary<string, bool>();
                    return;
                }
                var json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, bool>>(json);
                _sectionState = loaded ?? new Dictionary<string, bool>();
            }
            catch (Exception ex)
            {
                LogWarn("LoadSectionState failed: " + ex.Message);
                _sectionState = new Dictionary<string, bool>();
            }
        }

        private void SaveSectionState()
        {
            try
            {
                File.WriteAllText(GetSectionStatePath(), JsonConvert.SerializeObject(_sectionState, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LogWarn("SaveSectionState failed: " + ex.Message);
            }
        }

        // #V.3 — analytics-cache.json. Whole-tab snapshot persisted on every successful refresh; loaded
        // on tab open BEFORE the API fetch fires so users see data instantly. Only applied when
        // savedAt < 24h old AND rangeLabel matches the current selected range.
        private string GetAnalyticsCachePath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = System.IO.Path.Combine(appDataPath, "Socinator1.0", "Fanvue");
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            return System.IO.Path.Combine(dir, "analytics-cache.json");
        }

        private bool LoadAnalyticsCache()
        {
            try
            {
                var path = GetAnalyticsCachePath();
                if (!File.Exists(path)) { return false; }
                var json = File.ReadAllText(path);
                var cache = JsonConvert.DeserializeObject<AnalyticsCacheDto>(json);
                if (cache == null) { return false; }

                if ((DateTime.UtcNow - cache.SavedAt).TotalHours > 24)
                {
                    GlobusLogHelper.log.Debug(LogTag + " Cache stale (>24h old) — skipping hydrate");
                    return false;
                }
                string currentRangeLabel = _selectedTimeRange != null ? _selectedTimeRange.Label : "(none)";
                if (cache.RangeLabel != currentRangeLabel)
                {
                    GlobusLogHelper.log.Debug(LogTag + " Cache range mismatch (cache=\"" + cache.RangeLabel
                        + "\" vs current=\"" + currentRangeLabel + "\") — skipping hydrate");
                    return false;
                }
                if (cache.Accounts == null || cache.Accounts.Count == 0) { return false; }

                // Hydrate _accounts list. Each cached entry becomes an AccountData with its dicts repopulated.
                _accounts = new List<AccountData>();
                _totalFollowers = 0;
                _totalSubscribers = 0;
                _totalGrossEarnings = 0;
                _totalNetEarnings = 0;
                _monthGrossEarnings = 0;
                _monthlyEarnings.Clear();
                _monthlyEarningsNet.Clear();
                _breakdownBySource.Clear();
                _earningsByType.Clear();
                _breakdownBySourceNet.Clear();
                _earningsByTypeNet.Clear();
                _bucketKeyToDate.Clear();
                _chartSubscriberInsightRows.Clear();

                foreach (var ca in cache.Accounts)
                {
                    if (ca == null) { continue; }
                    var acc = new AccountData
                    {
                        Username = ca.Username,
                        Followers = ca.Followers,
                        Subscribers = ca.Subscribers,
                        TotalGross = ca.TotalGross,
                        TotalNet = ca.TotalNet,
                        MonthGross = ca.MonthGross,
                        MonthNet = ca.MonthNet
                    };
                    if (ca.MonthlyEarnings != null) { foreach (var kv in ca.MonthlyEarnings) acc.MonthlyEarnings[kv.Key] = kv.Value; }
                    if (ca.MonthlyEarningsNet != null) { foreach (var kv in ca.MonthlyEarningsNet) acc.MonthlyEarningsNet[kv.Key] = kv.Value; }
                    if (ca.BreakdownBySource != null) { foreach (var kv in ca.BreakdownBySource) acc.BreakdownBySource[kv.Key] = kv.Value; }
                    if (ca.BreakdownBySourceNet != null) { foreach (var kv in ca.BreakdownBySourceNet) acc.BreakdownBySourceNet[kv.Key] = kv.Value; }
                    if (ca.EarningsByType != null) { foreach (var kv in ca.EarningsByType) acc.EarningsByType[kv.Key] = kv.Value; }
                    if (ca.EarningsByTypeNet != null) { foreach (var kv in ca.EarningsByTypeNet) acc.EarningsByTypeNet[kv.Key] = kv.Value; }
                    if (ca.BucketKeyToDate != null) { foreach (var kv in ca.BucketKeyToDate) acc.BucketKeyToDate[kv.Key] = kv.Value; }
                    _accounts.Add(acc);

                    // Aggregate into tab-level dicts so DrawCharts can render immediately.
                    _totalFollowers += acc.Followers;
                    _totalSubscribers += acc.Subscribers;
                    _totalGrossEarnings += acc.TotalGross;
                    _totalNetEarnings += acc.TotalNet;
                    _monthGrossEarnings += acc.MonthGross;
                    foreach (var kv in acc.MonthlyEarnings)
                    {
                        if (!_monthlyEarnings.ContainsKey(kv.Key)) _monthlyEarnings[kv.Key] = 0;
                        _monthlyEarnings[kv.Key] += kv.Value;
                    }
                    foreach (var kv in acc.MonthlyEarningsNet)
                    {
                        if (!_monthlyEarningsNet.ContainsKey(kv.Key)) _monthlyEarningsNet[kv.Key] = 0;
                        _monthlyEarningsNet[kv.Key] += kv.Value;
                    }
                    foreach (var kv in acc.BreakdownBySource)
                    {
                        if (!_breakdownBySource.ContainsKey(kv.Key)) _breakdownBySource[kv.Key] = 0;
                        _breakdownBySource[kv.Key] += kv.Value;
                    }
                    foreach (var kv in acc.BreakdownBySourceNet)
                    {
                        if (!_breakdownBySourceNet.ContainsKey(kv.Key)) _breakdownBySourceNet[kv.Key] = 0;
                        _breakdownBySourceNet[kv.Key] += kv.Value;
                    }
                    foreach (var kv in acc.EarningsByType)
                    {
                        if (!_earningsByType.ContainsKey(kv.Key)) _earningsByType[kv.Key] = 0;
                        _earningsByType[kv.Key] += kv.Value;
                    }
                    foreach (var kv in acc.EarningsByTypeNet)
                    {
                        if (!_earningsByTypeNet.ContainsKey(kv.Key)) _earningsByTypeNet[kv.Key] = 0;
                        _earningsByTypeNet[kv.Key] += kv.Value;
                    }
                    foreach (var kv in acc.BucketKeyToDate)
                    {
                        if (!_bucketKeyToDate.ContainsKey(kv.Key)) _bucketKeyToDate[kv.Key] = kv.Value;
                    }
                }
                GlobusLogHelper.log.Debug(LogTag + " LoadAnalyticsCache: hydrated " + cache.Accounts.Count
                    + " account(s), savedAt=" + cache.SavedAt.ToString("o") + " range=" + cache.RangeLabel);
                return true;
            }
            catch (Exception ex)
            {
                LogWarn("LoadAnalyticsCache failed: " + ex.Message);
                return false;
            }
        }

        private void SaveAnalyticsCache()
        {
            try
            {
                var cache = new AnalyticsCacheDto
                {
                    SavedAt = DateTime.UtcNow,
                    RangeLabel = _selectedTimeRange != null ? _selectedTimeRange.Label : "(none)",
                    Accounts = new List<AnalyticsCacheAccountDto>()
                };
                if (_accounts != null)
                {
                    foreach (var acc in _accounts)
                    {
                        if (acc == null) { continue; }
                        cache.Accounts.Add(new AnalyticsCacheAccountDto
                        {
                            Username = acc.Username,
                            Followers = acc.Followers,
                            Subscribers = acc.Subscribers,
                            TotalGross = acc.TotalGross,
                            TotalNet = acc.TotalNet,
                            MonthGross = acc.MonthGross,
                            MonthNet = acc.MonthNet,
                            MonthlyEarnings = new Dictionary<string, decimal>(acc.MonthlyEarnings),
                            MonthlyEarningsNet = new Dictionary<string, decimal>(acc.MonthlyEarningsNet),
                            BreakdownBySource = new Dictionary<string, decimal>(acc.BreakdownBySource),
                            BreakdownBySourceNet = new Dictionary<string, decimal>(acc.BreakdownBySourceNet),
                            EarningsByType = new Dictionary<string, decimal>(acc.EarningsByType),
                            EarningsByTypeNet = new Dictionary<string, decimal>(acc.EarningsByTypeNet),
                            BucketKeyToDate = new Dictionary<string, DateTime>(acc.BucketKeyToDate)
                        });
                    }
                }
                File.WriteAllText(GetAnalyticsCachePath(), JsonConvert.SerializeObject(cache, Formatting.Indented));
                GlobusLogHelper.log.Debug(LogTag + " SaveAnalyticsCache: wrote " + cache.Accounts.Count + " account(s)");
            }
            catch (Exception ex)
            {
                LogWarn("SaveAnalyticsCache failed: " + ex.Message);
            }
        }

        private void ApplyPersistedSectionState()
        {
            // Apply persisted expand/collapse state to each of the four sections. Each Apply* helper
            // resolves the state from _sectionState (defaults to expanded when missing) and syncs the
            // body Visibility + chevron glyph.
            ApplyEarningsSectionVisibility();
            ApplyRevenueSectionVisibility();
            ApplyAccountsSectionVisibility();
            ApplyBottomListSectionVisibility();
        }

        private void ApplyBottomListVisibility()
        {
            // Toggle the two stacked lists, their search boxes, the contextual "all-time" hint, and re-evaluate
            // the no-data placeholders (only the active list's empty-state should show).
            bool showTop = _bottomList == "TopSpenders";

            if (TopSpendersScroll != null)        { TopSpendersScroll.Visibility        = showTop ? Visibility.Visible : Visibility.Collapsed; }
            if (RecentSubscribersScroll != null)  { RecentSubscribersScroll.Visibility  = showTop ? Visibility.Collapsed : Visibility.Visible; }
            if (TxtTopSpendersSearch != null)     { TxtTopSpendersSearch.Visibility     = showTop ? Visibility.Visible : Visibility.Collapsed; }
            if (TxtRecentSubscribersSearch != null) { TxtRecentSubscribersSearch.Visibility = showTop ? Visibility.Collapsed : Visibility.Visible; }
            if (TxtTopSpendersHint != null)       { TxtTopSpendersHint.Visibility       = showTop ? Visibility.Visible : Visibility.Collapsed; }

            // No-data placeholders: only the active list's placeholder may show, and only if its source is empty.
            int topCount = TopSpendersList != null && TopSpendersList.ItemsSource is System.Collections.IEnumerable topSrc ? CountItems(topSrc) : 0;
            int subCount = RecentSubscribersList != null && RecentSubscribersList.ItemsSource is System.Collections.IEnumerable subSrc ? CountItems(subSrc) : 0;

            if (TxtNoTopSpenders != null)   { TxtNoTopSpenders.Visibility   = (showTop && topCount == 0) ? Visibility.Visible : Visibility.Collapsed; }
            if (TxtNoSubscribers != null)   { TxtNoSubscribers.Visibility   = (!showTop && subCount == 0) ? Visibility.Visible : Visibility.Collapsed; }
        }

        private static int CountItems(System.Collections.IEnumerable source)
        {
            if (source == null) { return 0; }
            int count = 0;
            foreach (var _ in source) { count++; }
            return count;
        }

        private async void CmbTimeRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            GlobusLogHelper.log.Debug(LogTag + " CmbTimeRange changed");
            var selected = CmbTimeRange.SelectedItem as ComboBoxItem;
            if (selected == null) { return; }
            var option = selected.Tag as TimeRangeOption;
            if (option == null) { return; }
            _selectedTimeRange = option;
            GlobusLogHelper.log.Debug(LogTag + " range=\"" + option.Label + "\" -> triggering re-fetch (granularity will be picked by FetchAccountData based on totalDays)");

            // Skip the auto-refresh during initial population (before accounts load).
            if (_accounts.Count == 0) { return; }
            await RefreshData();
        }

        #endregion

        #region Data Refresh

        private async Task RefreshData()
        {
            if (_accounts.Count == 0) return;

            BtnRefresh.IsEnabled = false;
            LoadingOverlay.Visibility = Visibility.Visible;

            // #V.7 — snapshot prior good state when triggered manually so we can restore on failure.
            // Cache hydrate (initial-load path) doesn't need a snapshot — there's nothing to lose
            // since the user just opened the tab.
            RefreshDataSnapshot priorState = null;
            if (_manualRefreshInFlight)
            {
                priorState = CapturePriorRefreshState();
            }

            try
            {
                _cancellationTokenSource = new CancellationTokenSource();

                // Reset aggregates
                _totalFollowers = 0;
                _totalSubscribers = 0;
                _totalGrossEarnings = 0;
                _totalNetEarnings = 0;
                _monthGrossEarnings = 0;
                _prevMonthGrossEarnings = 0;
                _subscriberDelta = 0;
                _monthlyEarnings.Clear();
                _monthlyEarningsNet.Clear();
                _breakdownBySource.Clear();
                _earningsByType.Clear();
                _breakdownBySourceNet.Clear();
                _earningsByTypeNet.Clear();
                _bucketKeyToDate.Clear();
                _bucketDates.Clear();
                _chartSubscriberInsightRows.Clear();
                _chartSubscriberRangeTruncated = false;
                _lastFetchFailed = false;
                // #V.4 — banner auto-clears at the start of a refresh; will be re-shown post-fetch
                // if any account had EarningsFetchFailed = true.
                HideApiErrorBanner();

                if (_selectedAccountKey == AllAccountsKey)
                {
                    // Fetch data for all accounts
                    var accountBreakdowns = new List<AccountBreakdownItem>();

                    for (int i = 0; i < _accounts.Count; i++)
                    {
                        TxtLoadingStatus.Text = "Fetching " + _accounts[i].Username + " (" + (i + 1) + "/" + _accounts.Count + ")...";
                        await FetchAccountData(_accounts[i], _cancellationTokenSource.Token);

                        accountBreakdowns.Add(new AccountBreakdownItem
                        {
                            Username = "@" + _accounts[i].Username,
                            Followers = FormatCount(_accounts[i].Followers),
                            Subscribers = FormatCount(_accounts[i].Subscribers),
                            MonthEarnings = FormatMoney(_accounts[i].MonthGross),
                            TotalEarnings = FormatMoney(_accounts[i].TotalGross),
                            StatusColor = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#28A745"))
                        });

                        // Aggregate
                        _totalFollowers += _accounts[i].Followers;
                        _totalSubscribers += _accounts[i].Subscribers;
                        _totalGrossEarnings += _accounts[i].TotalGross;
                        _totalNetEarnings += _accounts[i].TotalNet;
                        _monthGrossEarnings += _accounts[i].MonthGross;
                        _prevMonthGrossEarnings += _accounts[i].PrevMonthGross;
                        _subscriberDelta += _accounts[i].SubscriberDelta;

                        // Aggregate monthly earnings (gross)
                        foreach (var kvp in _accounts[i].MonthlyEarnings)
                        {
                            if (!_monthlyEarnings.ContainsKey(kvp.Key))
                                _monthlyEarnings[kvp.Key] = 0;
                            _monthlyEarnings[kvp.Key] += kvp.Value;
                        }
                        // Merge bucket-key -> DateTime so DrawXAxisLabels has real DateTimes (#P).
                        foreach (var kvp in _accounts[i].BucketKeyToDate)
                        {
                            if (!_bucketKeyToDate.ContainsKey(kvp.Key))
                                _bucketKeyToDate[kvp.Key] = kvp.Value;
                        }
                        // Aggregate monthly earnings (net) — parallel buckets for multi-series chart.
                        foreach (var kvp in _accounts[i].MonthlyEarningsNet)
                        {
                            if (!_monthlyEarningsNet.ContainsKey(kvp.Key))
                                _monthlyEarningsNet[kvp.Key] = 0;
                            _monthlyEarningsNet[kvp.Key] += kvp.Value;
                        }
                        // Aggregate source/type breakdowns (gross + net — net feeds the pie's Gross/Net toggle #O.2)
                        foreach (var kvp in _accounts[i].BreakdownBySource)
                        {
                            if (!_breakdownBySource.ContainsKey(kvp.Key)) _breakdownBySource[kvp.Key] = 0;
                            _breakdownBySource[kvp.Key] += kvp.Value;
                        }
                        foreach (var kvp in _accounts[i].BreakdownBySourceNet)
                        {
                            if (!_breakdownBySourceNet.ContainsKey(kvp.Key)) _breakdownBySourceNet[kvp.Key] = 0;
                            _breakdownBySourceNet[kvp.Key] += kvp.Value;
                        }
                        foreach (var kvp in _accounts[i].EarningsByType)
                        {
                            if (!_earningsByType.ContainsKey(kvp.Key)) _earningsByType[kvp.Key] = 0;
                            _earningsByType[kvp.Key] += kvp.Value;
                        }
                        foreach (var kvp in _accounts[i].EarningsByTypeNet)
                        {
                            if (!_earningsByTypeNet.ContainsKey(kvp.Key)) _earningsByTypeNet[kvp.Key] = 0;
                            _earningsByTypeNet[kvp.Key] += kvp.Value;
                        }
                        // Aggregate per-account SubscriberInsight rows for the chart's "Subscribers Gained"
                        // series (#L). Same rows used by RANGE-TOTALS but at chart scope, not GrowthTracker.
                        if (_accounts[i].SubscriberInsightRows != null)
                        {
                            _chartSubscriberInsightRows.AddRange(_accounts[i].SubscriberInsightRows);
                        }
                    }

                    AccountBreakdownList.ItemsSource = accountBreakdowns;
                    InstallFilter(AccountBreakdownList, o =>
                    {
                        var item = o as AccountBreakdownItem;
                        return item != null && MatchesText(item.Username, _accountBreakdownSearch);
                    });
                }
                else
                {
                    // Fetch data for selected account only
                    var account = _accounts.FirstOrDefault(x => x.Username == _selectedAccountKey);
                    if (account != null)
                    {
                        TxtLoadingStatus.Text = "Fetching @" + account.Username + "...";
                        await FetchAccountData(account, _cancellationTokenSource.Token);

                        _totalFollowers = account.Followers;
                        _totalSubscribers = account.Subscribers;
                        _totalGrossEarnings = account.TotalGross;
                        _totalNetEarnings = account.TotalNet;
                        _monthGrossEarnings = account.MonthGross;
                        _prevMonthGrossEarnings = account.PrevMonthGross;
                        _subscriberDelta = account.SubscriberDelta;
                        _monthlyEarnings = new Dictionary<string, decimal>(account.MonthlyEarnings);
                        _monthlyEarningsNet = new Dictionary<string, decimal>(account.MonthlyEarningsNet);
                        _bucketKeyToDate = new Dictionary<string, DateTime>(account.BucketKeyToDate);
                        _breakdownBySource = new Dictionary<string, decimal>(account.BreakdownBySource);
                        _breakdownBySourceNet = new Dictionary<string, decimal>(account.BreakdownBySourceNet);
                        _earningsByType = new Dictionary<string, decimal>(account.EarningsByType);
                        _earningsByTypeNet = new Dictionary<string, decimal>(account.EarningsByTypeNet);
                        if (account.SubscriberInsightRows != null)
                        {
                            _chartSubscriberInsightRows.AddRange(account.SubscriberInsightRows);
                        }
                    }
                }

                // #U.2 + #V.4: surface per-account fetch failures to the tab-level flag and to the
                // API error banner at the top of the tab.
                if (_selectedAccountKey == AllAccountsKey)
                {
                    int failedCount = _accounts.Count(a => a.EarningsFetchFailed);
                    if (failedCount > 0)
                    {
                        _lastFetchFailed = true;
                        GlobusLogHelper.log.Debug(LogTag + " Fetch failed — cleared stale dicts to prevent ghost data; UI shows error state. (failedAccounts="
                            + failedCount + "/" + _accounts.Count + ")");
                        ShowApiErrorBanner("API error — Fanvue returned an error for "
                            + failedCount + "/" + _accounts.Count + " account(s). Showing partial data.");
                    }
                }
                else
                {
                    var sel = _accounts.FirstOrDefault(x => x.Username == _selectedAccountKey);
                    if (sel != null && sel.EarningsFetchFailed)
                    {
                        _lastFetchFailed = true;
                        GlobusLogHelper.log.Debug(LogTag + " Fetch failed — cleared stale dicts to prevent ghost data; UI shows error state. (account=" + sel.Username + ")");
                        ShowApiErrorBanner("API error — Fanvue couldn't return earnings for @" + sel.Username + ".");
                    }
                }

                // Build the parallel _bucketDates list (#Q) — sorted ascending, deduplicated.
                // Each entry is the API's `periodStart` for that bucket. Bucket order matches
                // _monthlyEarnings.Keys insertion order (the API returns oldest-to-newest).
                _bucketDates.Clear();
                foreach (var key in _monthlyEarnings.Keys)
                {
                    DateTime d;
                    if (_bucketKeyToDate.TryGetValue(key, out d) && d != DateTime.MinValue)
                    {
                        _bucketDates.Add(d);
                    }
                }
                _bucketDates = _bucketDates.OrderBy(d => d).ToList();

                UpdateUI();
                DrawCharts();

                // Persist today's follower/subscriber counts so the Growth Tracker has historical
                // data to compute deltas against next time. Then refresh the Growth Tracker tiles
                // for the currently-selected window.
                UpsertTodaySnapshots();
                await RefreshGrowthTrackerAsync();

                // #V.5 — fetch + populate the always-visible 24h stat tiles. Independent of CmbTimeRange.
                await Refresh24HourStatsAsync(_cancellationTokenSource != null ? _cancellationTokenSource.Token : System.Threading.CancellationToken.None);

                // #V.3 — persist a snapshot of the just-fetched data so the next tab open hydrates instantly.
                SaveAnalyticsCache();

                TxtLastUpdated.Text = "Updated " + DateTime.Now.ToString("h:mm tt");
                if (TxtCacheStatus != null)
                {
                    // #V.7: reset the gold "Refreshing…" / "Loaded from cache · Updating…" text and hide.
                    TxtCacheStatus.Text = string.Empty;
                    TxtCacheStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888"));
                    TxtCacheStatus.Visibility = Visibility.Collapsed;
                }
                LogInfo("Refresh completed");
            }
            catch (OperationCanceledException)
            {
                LogWarn("Refresh cancelled");
                if (priorState != null)
                {
                    RestorePriorRefreshState(priorState);
                    UpdateUI();
                    DrawCharts();
                }
            }
            catch (Exception ex)
            {
                LogError("Refresh failed", ex);
                if (priorState != null)
                {
                    RestorePriorRefreshState(priorState);
                    UpdateUI();
                    DrawCharts();
                    ShowApiErrorBanner("API error — Fanvue request failed. Showing last-known-good data.");
                }
            }
            finally
            {
                BtnRefresh.IsEnabled = true;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                _cancellationTokenSource = null;
            }
        }

        // #V.7 — snapshot of the tab-level aggregate state before a manual refresh starts. Restored
        // on cancel/exception so the user keeps seeing their last-known-good data after a failed
        // manual refresh (cache file itself isn't touched on failure either, since SaveAnalyticsCache
        // only runs on the success path).
        private class RefreshDataSnapshot
        {
            public int TotalFollowers;
            public int TotalSubscribers;
            public decimal TotalGrossEarnings;
            public decimal TotalNetEarnings;
            public decimal MonthGrossEarnings;
            public decimal PrevMonthGrossEarnings;
            public int SubscriberDelta;
            public Dictionary<string, decimal> MonthlyEarnings;
            public Dictionary<string, decimal> MonthlyEarningsNet;
            public Dictionary<string, decimal> BreakdownBySource;
            public Dictionary<string, decimal> EarningsByType;
            public Dictionary<string, decimal> BreakdownBySourceNet;
            public Dictionary<string, decimal> EarningsByTypeNet;
            public Dictionary<string, DateTime> BucketKeyToDate;
            public List<DateTime> BucketDates;
            public List<SubscriberEventRowDto> ChartSubscriberInsightRows;
            public bool ChartSubscriberRangeTruncated;
        }

        private RefreshDataSnapshot CapturePriorRefreshState()
        {
            return new RefreshDataSnapshot
            {
                TotalFollowers = _totalFollowers,
                TotalSubscribers = _totalSubscribers,
                TotalGrossEarnings = _totalGrossEarnings,
                TotalNetEarnings = _totalNetEarnings,
                MonthGrossEarnings = _monthGrossEarnings,
                PrevMonthGrossEarnings = _prevMonthGrossEarnings,
                SubscriberDelta = _subscriberDelta,
                MonthlyEarnings = new Dictionary<string, decimal>(_monthlyEarnings),
                MonthlyEarningsNet = new Dictionary<string, decimal>(_monthlyEarningsNet),
                BreakdownBySource = new Dictionary<string, decimal>(_breakdownBySource),
                EarningsByType = new Dictionary<string, decimal>(_earningsByType),
                BreakdownBySourceNet = new Dictionary<string, decimal>(_breakdownBySourceNet),
                EarningsByTypeNet = new Dictionary<string, decimal>(_earningsByTypeNet),
                BucketKeyToDate = new Dictionary<string, DateTime>(_bucketKeyToDate),
                BucketDates = new List<DateTime>(_bucketDates),
                ChartSubscriberInsightRows = new List<SubscriberEventRowDto>(_chartSubscriberInsightRows),
                ChartSubscriberRangeTruncated = _chartSubscriberRangeTruncated
            };
        }

        private void RestorePriorRefreshState(RefreshDataSnapshot s)
        {
            if (s == null) { return; }
            _totalFollowers = s.TotalFollowers;
            _totalSubscribers = s.TotalSubscribers;
            _totalGrossEarnings = s.TotalGrossEarnings;
            _totalNetEarnings = s.TotalNetEarnings;
            _monthGrossEarnings = s.MonthGrossEarnings;
            _prevMonthGrossEarnings = s.PrevMonthGrossEarnings;
            _subscriberDelta = s.SubscriberDelta;
            _monthlyEarnings = s.MonthlyEarnings ?? new Dictionary<string, decimal>();
            _monthlyEarningsNet = s.MonthlyEarningsNet ?? new Dictionary<string, decimal>();
            _breakdownBySource = s.BreakdownBySource ?? new Dictionary<string, decimal>();
            _earningsByType = s.EarningsByType ?? new Dictionary<string, decimal>();
            _breakdownBySourceNet = s.BreakdownBySourceNet ?? new Dictionary<string, decimal>();
            _earningsByTypeNet = s.EarningsByTypeNet ?? new Dictionary<string, decimal>();
            _bucketKeyToDate = s.BucketKeyToDate ?? new Dictionary<string, DateTime>();
            _bucketDates = s.BucketDates ?? new List<DateTime>();
            _chartSubscriberInsightRows = s.ChartSubscriberInsightRows ?? new List<SubscriberEventRowDto>();
            _chartSubscriberRangeTruncated = s.ChartSubscriberRangeTruncated;
            _lastFetchFailed = false; // restored data is good — no banner needed beyond the failure-message banner
            GlobusLogHelper.log.Debug(LogTag + " RestorePriorRefreshState: restored " + _monthlyEarnings.Count + " buckets, "
                + _breakdownBySource.Count + " sources from manual-refresh snapshot");
        }

        private async Task FetchAccountData(AccountData account, CancellationToken token)
        {
            try
            {
                var apiClient = new FanvueApiClient(_authService);
                apiClient.Credentials = account.Credentials;

                // Fetch profile (followers, subscribers)
                var profileResponse = await apiClient.GetCurrentUserAsync(token);
                if (profileResponse.IsSuccess && profileResponse.Data != null)
                {
                    var fanCounts = profileResponse.Data["fanCounts"] as JObject;
                    if (fanCounts != null)
                    {
                        account.Followers = fanCounts["followersCount"]?.Value<int>() ?? 0;
                        account.Subscribers = fanCounts["subscribersCount"]?.Value<int>() ?? 0;
                    }

                    // Get recent followers
                    account.RecentFollowers.Clear();
                    var followersResponse = await apiClient.GetFollowersAsync(token, 10);
                    if (followersResponse.IsSuccess && followersResponse.Data != null)
                    {
                        var items = followersResponse.Data["data"] as JArray;
                        if (items != null)
                        {
                            foreach (var item in items)
                            {
                                var jobj = item as JObject;
                                var username = GetJsonValue(jobj, "handle", "username") ?? "Unknown";
                                var registeredAt = GetJsonValue(jobj, "registeredAt", "createdAt") ?? "";
                                account.RecentFollowers.Add(new RecentItem
                                {
                                    Username = "@" + username,
                                    Time = FormatDate(registeredAt)
                                });
                            }
                        }
                    }

                    // Get recent subscribers
                    account.RecentSubscribers.Clear();
                    var subscribersResponse = await apiClient.GetSubscribersAsync(token, 10);
                    if (subscribersResponse.IsSuccess && subscribersResponse.Data != null)
                    {
                        var items = subscribersResponse.Data["data"] as JArray;
                        if (items != null)
                        {
                            foreach (var item in items)
                            {
                                var jobj = item as JObject;
                                var username = GetJsonValue(jobj, "handle", "username") ?? "Unknown";
                                var registeredAt = GetJsonValue(jobj, "registeredAt", "subscribedAt") ?? "";
                                account.RecentSubscribers.Add(new RecentItem
                                {
                                    Username = "@" + username,
                                    Time = FormatDate(registeredAt)
                                });
                            }
                        }
                    }
                }

                // Fetch earnings summary (replaces 20-page loop)
                account.TotalGross = 0;
                account.TotalNet = 0;
                account.MonthGross = 0;
                account.MonthNet = 0;
                account.PrevMonthGross = 0;
                account.MonthlyEarnings.Clear();
                account.BucketKeyToDate.Clear();
                account.MonthlyEarningsNet.Clear();
                // #U.2: clear source/type breakdowns up front too. Previously these were cleared INSIDE
                // the success branch only — when the API failed, the account kept its prior fetch's
                // BreakdownBySource and the aggregation loop summed those stale values into the tab
                // dicts, producing the "$936 sources / $0 bars" ghost-data symptom.
                account.BreakdownBySource.Clear();
                account.BreakdownBySourceNet.Clear();
                account.EarningsByType.Clear();
                account.EarningsByTypeNet.Clear();
                account.EarningsFetchFailed = false;

                var now = DateTime.UtcNow;
                var firstOfMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var firstOfPrevMonth = firstOfMonth.AddMonths(-1);

                // Compute selected range + granularity ONCE for this account fetch.
                var range = (_selectedTimeRange ?? _timeRanges[0]).ComputeRange(now);
                var rangeStart = range.start;
                var rangeEnd = range.end;
                var totalDays = (rangeEnd - rangeStart).TotalDays;

                // Fanvue API only accepts granularity values "day" or "week" (verified live 2026-04-28).
                // "month" / "monthly" => 400 Bad Request. For long ranges we fall back to "week".
                string requestedGranularity;
                string bucketFormat;
                if (totalDays <= 31)
                {
                    requestedGranularity = "day";
                    bucketFormat = "MMM d";
                }
                else if (totalDays > 365)
                {
                    // #S — reverted from "MMM yy" back to "MMM d, yy". The "MMM yy" format caused
                    // weekly buckets within the same month to share a label, effectively collapsing
                    // them to monthly granularity (user observed this on All Time and didn't want it).
                    // Density-skip + auto-rotate from #P keep the chart readable without losing data.
                    requestedGranularity = "week";
                    bucketFormat = "MMM d, yy";
                }
                else
                {
                    requestedGranularity = "week";
                    bucketFormat = "MMM d";
                }

                // Pre-emptive fallback: any "month" granularity is rejected by the API. Force "week"
                // before the call rather than catching a 400. Logged so future edits can't silently
                // reintroduce the bad code path.
                string granularity = requestedGranularity;
                if (granularity == "month")
                {
                    GlobusLogHelper.log.Debug(LogTag + " Granularity \"month\" rejected by API — falling back to \"week\"");
                    granularity = "week";
                }
                _currentBucketFormat = bucketFormat;
                _currentGranularity = granularity;

                var rangeLabel = _selectedTimeRange != null ? _selectedTimeRange.Label : "(default)";
                GlobusLogHelper.log.Debug(LogTag + " Range \"" + rangeLabel + "\" -> start="
                    + rangeStart.ToString("yyyy-MM-ddTHH:mm:ssZ") + " end="
                    + rangeEnd.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    + " granularity=" + granularity + " totalDays=" + ((int)totalDays));

                try
                {
                    // #U.1 / #U.3: chunked + retry-once helper. When totalDays > 365 the API 500s on
                    // a single span, so we split into 365-day windows, fire concurrently, and merge
                    // results client-side. Each chunk retries once (500ms delay) on null/non-2xx.
                    await FetchEarningsSummaryWithChunking(apiClient, token, account,
                        rangeStart, rangeEnd, granularity, bucketFormat, totalDays);
                }
                catch (Exception ex)
                {
                    LogError("GetEarningsSummaryAsync failed", ex);
                    account.EarningsFetchFailed = true;
                }

                // Subscriber insights -> SubscriberDelta across the selected range.
                account.SubscriberDelta = 0;
                account.NewSubscribersInRange = 0;
                account.CancelledSubscribersInRange = 0;
                account.SubscriberInsightRows.Clear();
                try
                {
                    int subSize = (int)Math.Min(50, Math.Ceiling(totalDays + 1));
                    if (subSize < 1) { subSize = 1; }
                    var subUrl = "/insights/subscribers?startDate=" + rangeStart.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        + "&endDate=" + rangeEnd.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        + "&size=" + subSize;
                    GlobusLogHelper.log.Debug(LogTag + " SubscriberInsights request -> " + subUrl);
                    var subInsightsResp = await apiClient.GetSubscriberInsightsAsync(token, rangeStart, rangeEnd, subSize, null);
                    int subStatus = (subInsightsResp != null && subInsightsResp.IsSuccess) ? 200 : 0;
                    if (subInsightsResp.IsSuccess && subInsightsResp.Data != null && subInsightsResp.Data.Data != null)
                    {
                        int newCount = 0;
                        int cancelledCount = 0;
                        foreach (var row in subInsightsResp.Data.Data)
                        {
                            newCount += row.NewSubscribersCount;
                            cancelledCount += row.CancelledSubscribersCount;
                            account.SubscriberInsightRows.Add(row); // chart-scope cache (#L)
                        }
                        account.NewSubscribersInRange = newCount;
                        account.CancelledSubscribersInRange = cancelledCount;
                        account.SubscriberDelta = newCount - cancelledCount;
                        GlobusLogHelper.log.Debug(LogTag + " SubscriberInsights response status=" + subStatus
                            + " new=" + newCount + " cancelled=" + cancelledCount
                            + " eventCount=" + subInsightsResp.Data.Data.Count);
                        LogInfo("Subscriber insights: new=" + newCount + " cancelled=" + cancelledCount + " delta=" + account.SubscriberDelta + " size=" + subSize);

                        if (totalDays > 365)
                        {
                            LogWarn("SubscriberInsights truncated at 365 days; range was " + ((int)totalDays) + " days.");
                            _chartSubscriberRangeTruncated = true;
                        }
                    }
                    else
                    {
                        var body = subInsightsResp != null ? (subInsightsResp.ErrorMessage ?? "") : "";
                        if (body.Length > 500) { body = body.Substring(0, 500); }
                        GlobusLogHelper.log.Debug(LogTag + " API failure code=" + subStatus + " body=" + body);
                        LogWarn("GetSubscriberInsightsAsync returned no data");
                    }
                }
                catch (Exception ex)
                {
                    LogError("GetSubscriberInsightsAsync failed", ex);
                }

                // Recent spending (single page, 20 rows) — uses the selected range.
                account.RecentSpending.Clear();
                try
                {
                    var spendingResp = await apiClient.GetSpendingAsync(token, rangeStart, rangeEnd, 20, null);
                    if (spendingResp.IsSuccess && spendingResp.Data != null && spendingResp.Data.Data != null)
                    {
                        foreach (var row in spendingResp.Data.Data)
                        {
                            var fanLabel = row.User != null && !string.IsNullOrEmpty(row.User.Handle)
                                ? "@" + row.User.Handle
                                : "Anonymous";
                            account.RecentSpending.Add(new RecentSpendingItem
                            {
                                Date = FormatDate(row.Date.ToString("o")),
                                Fan = fanLabel,
                                Source = string.IsNullOrEmpty(row.Source) ? "" : row.Source,
                                Gross = FormatMoney(row.Gross / 100m)
                            });
                        }
                        LogInfo("Recent spending rows: " + account.RecentSpending.Count);
                    }
                    else
                    {
                        LogWarn("GetSpendingAsync returned no data");
                    }
                }
                catch (Exception ex)
                {
                    LogError("GetSpendingAsync failed", ex);
                }

                // Top spenders (page 1, top 10)
                account.TopSpenders.Clear();
                try
                {
                    var topResp = await apiClient.GetTopSpendersAsync(token, 1, 10);
                    if (topResp.IsSuccess && topResp.Data != null && topResp.Data.Data != null)
                    {
                        int rank = 1;
                        foreach (var top in topResp.Data.Data)
                        {
                            var fanLabel = top.User != null && !string.IsNullOrEmpty(top.User.Handle)
                                ? "@" + top.User.Handle
                                : "Anonymous";
                            account.TopSpenders.Add(new TopSpenderItem
                            {
                                Rank = "#" + rank,
                                Fan = fanLabel,
                                TotalGross = FormatMoney(top.TotalGross / 100m)
                            });
                            rank++;
                        }
                        LogInfo("Top spenders rows: " + account.TopSpenders.Count);
                    }
                    else
                    {
                        LogWarn("GetTopSpendersAsync returned no data");
                    }
                }
                catch (Exception ex)
                {
                    LogError("GetTopSpendersAsync failed", ex);
                }
            }
            catch (Exception ex)
            {
                LogError("FetchAccountData failed for account index " + _accounts.IndexOf(account), ex);
            }
        }

        // #U.1 / #U.3 — chunked + retry-once EarningsSummary fetch. Fanvue's API 500s on multi-year
        // spans for /insights/earnings/summary so we split into 365-day windows when totalDays > 365,
        // fire concurrently, and merge each chunk's OverTime / BreakdownBySource / EarningsByType into
        // the account dicts. The LAST successful chunk seeds totals.{allTime,thisMonth} since it's
        // the one that spans up to "now". Each chunk retries once after a 500ms delay on null/non-2xx.
        // If ALL chunks fail, account.EarningsFetchFailed is set; the caller surfaces that to the
        // tab-level _lastFetchFailed flag for the chart/pie error-state UI.
        private async Task FetchEarningsSummaryWithChunking(FanvueApiClient apiClient, CancellationToken token,
            AccountData account, DateTime rangeStart, DateTime rangeEnd, string granularity,
            string bucketFormat, double totalDays)
        {
            var windows = new List<KeyValuePair<DateTime, DateTime>>();
            if (totalDays > 365)
            {
                var cursor = rangeStart;
                while (cursor < rangeEnd)
                {
                    var chunkEnd = cursor.AddDays(365);
                    if (chunkEnd > rangeEnd) { chunkEnd = rangeEnd; }
                    windows.Add(new KeyValuePair<DateTime, DateTime>(cursor, chunkEnd));
                    cursor = chunkEnd;
                }
                GlobusLogHelper.log.Debug(LogTag + " AllTime chunked into " + windows.Count + " windows");
            }
            else
            {
                windows.Add(new KeyValuePair<DateTime, DateTime>(rangeStart, rangeEnd));
                GlobusLogHelper.log.Debug(LogTag + " EarningsSummary single window (totalDays=" + ((int)totalDays) + ")");
            }

            var fetchTasks = new List<Task<ApiResponse<EarningsSummaryDto>>>();
            for (int wi = 0; wi < windows.Count; wi++)
            {
                int chunkIdx = wi + 1;
                var w = windows[wi];
                var summaryUrl = "/insights/earnings/summary?startDate=" + w.Key.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    + "&endDate=" + w.Value.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    + "&granularity=" + granularity;
                GlobusLogHelper.log.Debug(LogTag + " EarningsSummary chunk " + chunkIdx + "/" + windows.Count
                    + " request -> " + summaryUrl);
                fetchTasks.Add(FetchOneEarningsChunkWithRetry(apiClient, token, w.Key, w.Value, granularity, chunkIdx, windows.Count));
            }

            ApiResponse<EarningsSummaryDto>[] responses;
            try
            {
                responses = await Task.WhenAll(fetchTasks);
            }
            catch (Exception ex)
            {
                LogError("FetchEarningsSummaryWithChunking Task.WhenAll failed", ex);
                account.EarningsFetchFailed = true;
                return;
            }

            int successfulChunks = 0;
            int totalBuckets = 0;
            ApiResponse<EarningsSummaryDto> lastSuccess = null;
            for (int ci = 0; ci < responses.Length; ci++)
            {
                var resp = responses[ci];
                int s = (resp != null && resp.IsSuccess) ? 200 : 0;
                int b = (resp != null && resp.Data != null && resp.Data.OverTime != null) ? resp.Data.OverTime.Count : 0;
                int srcCount = (resp != null && resp.Data != null && resp.Data.BreakdownBySource != null) ? resp.Data.BreakdownBySource.Count : 0;
                GlobusLogHelper.log.Debug(LogTag + " EarningsSummary chunk " + (ci + 1) + "/" + responses.Length
                    + " status=" + s + " buckets=" + b + " sourcesCount=" + srcCount);
                if (resp == null || !resp.IsSuccess || resp.Data == null)
                {
                    var body = (resp != null) ? (resp.ErrorMessage ?? "") : "(null response)";
                    if (body.Length > 500) { body = body.Substring(0, 500); }
                    GlobusLogHelper.log.Debug(LogTag + " EarningsSummary chunk " + (ci + 1) + " failed code=" + s + " body=" + body);
                    continue;
                }

                successfulChunks++;
                lastSuccess = resp;
                totalBuckets += b;

                var data = resp.Data;
                var overTime = data.OverTime;
                if (overTime != null)
                {
                    foreach (var bucket in overTime)
                    {
                        var bucketGross = bucket.Gross / 100m;
                        var bucketNet = bucket.Net / 100m;
                        var bucketKey = bucket.Date.ToString(bucketFormat);
                        if (!account.MonthlyEarnings.ContainsKey(bucketKey))
                            account.MonthlyEarnings[bucketKey] = 0;
                        account.MonthlyEarnings[bucketKey] += bucketGross;
                        if (!account.MonthlyEarningsNet.ContainsKey(bucketKey))
                            account.MonthlyEarningsNet[bucketKey] = 0;
                        account.MonthlyEarningsNet[bucketKey] += bucketNet;
                        if (!account.BucketKeyToDate.ContainsKey(bucketKey))
                            account.BucketKeyToDate[bucketKey] = bucket.Date;
                    }
                }
                if (data.BreakdownBySource != null)
                {
                    foreach (var kv in data.BreakdownBySource)
                    {
                        var grossToken = kv.Value != null ? kv.Value["gross"] : null;
                        if (grossToken == null) continue;
                        var dollars = grossToken.Value<long>() / 100m;
                        if (dollars != 0)
                        {
                            if (!account.BreakdownBySource.ContainsKey(kv.Key)) account.BreakdownBySource[kv.Key] = 0;
                            account.BreakdownBySource[kv.Key] += dollars;
                        }
                        var netToken = kv.Value != null ? kv.Value["net"] : null;
                        if (netToken != null)
                        {
                            var netDollars = netToken.Value<long>() / 100m;
                            if (netDollars != 0)
                            {
                                if (!account.BreakdownBySourceNet.ContainsKey(kv.Key)) account.BreakdownBySourceNet[kv.Key] = 0;
                                account.BreakdownBySourceNet[kv.Key] += netDollars;
                            }
                        }
                    }
                }
                if (data.EarningsByType != null)
                {
                    foreach (var kv in data.EarningsByType)
                    {
                        var grossToken = kv.Value != null ? kv.Value["gross"] : null;
                        if (grossToken == null) continue;
                        var dollars = grossToken.Value<long>() / 100m;
                        if (dollars != 0)
                        {
                            if (!account.EarningsByType.ContainsKey(kv.Key)) account.EarningsByType[kv.Key] = 0;
                            account.EarningsByType[kv.Key] += dollars;
                        }
                        var netToken = kv.Value != null ? kv.Value["net"] : null;
                        if (netToken != null)
                        {
                            var netDollars = netToken.Value<long>() / 100m;
                            if (netDollars != 0)
                            {
                                if (!account.EarningsByTypeNet.ContainsKey(kv.Key)) account.EarningsByTypeNet[kv.Key] = 0;
                                account.EarningsByTypeNet[kv.Key] += netDollars;
                            }
                        }
                    }
                }
            }

            // Totals from the LAST successful chunk (it spans up to "now" so its allTime/thisMonth
            // are the freshest values). If no chunks succeeded, leave account totals at 0 and flag.
            if (lastSuccess != null && lastSuccess.Data != null && lastSuccess.Data.Totals != null)
            {
                var totals = lastSuccess.Data.Totals;
                if (totals.AllTime != null)
                {
                    account.TotalGross = totals.AllTime.Gross / 100m;
                    account.TotalNet = totals.AllTime.Net / 100m;
                }
                if (totals.ThisMonth != null)
                {
                    account.MonthGross = totals.ThisMonth.Gross / 100m;
                    account.MonthNet = totals.ThisMonth.Net / 100m;
                    account.PrevMonthGross = totals.ThisMonth.PreviousMonthGross / 100m;
                }
            }

            if (successfulChunks == 0)
            {
                account.EarningsFetchFailed = true;
                LogWarn("EarningsSummary all " + responses.Length + " chunk(s) failed for account " + account.Username);
            }
            else
            {
                LogInfo("Earnings summary (chunked): chunks=" + successfulChunks + "/" + responses.Length
                    + " buckets=" + totalBuckets + " total=" + account.TotalGross
                    + " thisMonth=" + account.MonthGross + " prevMonth=" + account.PrevMonthGross
                    + " granularity=" + granularity);
            }
        }

        // #V.5 — fetch the always-visible 24-hour stat tiles. Runs once per RefreshData, separate
        // from the chart range. Aggregates across all accounts when "All Accounts" is selected, else
        // limits to the selected account. Followers count uses the existing snapshot infrastructure
        // (snapshot ~1 day ago vs current).
        private async Task Refresh24HourStatsAsync(CancellationToken token)
        {
            try
            {
                var last24Start = DateTime.UtcNow.AddHours(-24);
                var last24End = DateTime.UtcNow;
                decimal earned = 0m;
                int subsDelta = 0;
                int followersDelta = 0;

                List<AccountData> targets;
                if (_selectedAccountKey == AllAccountsKey)
                {
                    targets = _accounts != null ? _accounts.ToList() : new List<AccountData>();
                }
                else
                {
                    var sel = _accounts != null ? _accounts.FirstOrDefault(a => a.Username == _selectedAccountKey) : null;
                    targets = sel != null ? new List<AccountData> { sel } : new List<AccountData>();
                }

                foreach (var acc in targets)
                {
                    if (acc == null || acc.Credentials == null) { continue; }
                    var apiClient = new FanvueApiClient(_authService);
                    apiClient.Credentials = acc.Credentials;
                    try
                    {
                        var earnings24 = await apiClient.GetEarningsSummaryAsync(token, last24Start, last24End, "day", null);
                        if (earnings24 != null && earnings24.IsSuccess && earnings24.Data != null && earnings24.Data.OverTime != null)
                        {
                            foreach (var b in earnings24.Data.OverTime)
                            {
                                earned += b.Gross / 100m;
                            }
                        }
                    }
                    catch (Exception ex) { LogWarn("Refresh24HourStatsAsync earnings call failed: " + ex.Message); }

                    try
                    {
                        var subs24 = await apiClient.GetSubscriberInsightsAsync(token, last24Start, last24End, 50, null);
                        if (subs24 != null && subs24.IsSuccess && subs24.Data != null && subs24.Data.Data != null)
                        {
                            foreach (var row in subs24.Data.Data)
                            {
                                subsDelta += (row.NewSubscribersCount - row.CancelledSubscribersCount);
                            }
                        }
                    }
                    catch (Exception ex) { LogWarn("Refresh24HourStatsAsync subs call failed: " + ex.Message); }

                    // Followers delta from snapshot history.
                    try
                    {
                        var baseline = FindBaselineSnapshot(acc.Username, DateTime.UtcNow.AddDays(-1));
                        if (baseline != null) { followersDelta += (acc.Followers - baseline.Followers); }
                    }
                    catch (Exception ex) { LogWarn("Refresh24HourStatsAsync follower-baseline lookup failed: " + ex.Message); }
                }

                if (TxtEarned24h != null) { TxtEarned24h.Text = FormatMoney(earned); }
                if (TxtSubs24h != null) { TxtSubs24h.Text = (subsDelta >= 0 ? "+" : "") + subsDelta.ToString(System.Globalization.CultureInfo.InvariantCulture); }
                if (TxtFollowers24h != null) { TxtFollowers24h.Text = (followersDelta >= 0 ? "+" : "") + followersDelta.ToString(System.Globalization.CultureInfo.InvariantCulture); }

                GlobusLogHelper.log.Debug(LogTag + " 24h stats: earned=$"
                    + earned.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + " subsGained=" + subsDelta + " followersGained=" + followersDelta);
            }
            catch (Exception ex)
            {
                LogError("Refresh24HourStatsAsync failed", ex);
            }
        }

        // Per-chunk fetch with single retry on null/non-2xx, 500ms delay between attempts (#U.3).
        private async Task<ApiResponse<EarningsSummaryDto>> FetchOneEarningsChunkWithRetry(
            FanvueApiClient apiClient, CancellationToken token, DateTime start, DateTime end,
            string granularity, int chunkIdx, int chunkCount)
        {
            ApiResponse<EarningsSummaryDto> resp = null;
            try
            {
                resp = await apiClient.GetEarningsSummaryAsync(token, start, end, granularity, null);
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Debug(LogTag + " AllTime chunk " + chunkIdx + "/" + chunkCount
                    + " attempt 1 threw: " + ex.Message);
            }

            bool needsRetry = (resp == null) || !resp.IsSuccess;
            if (!needsRetry) { return resp; }

            int prevStatus = (resp != null && resp.IsSuccess) ? 200 : 0;
            GlobusLogHelper.log.Debug(LogTag + " AllTime chunk " + chunkIdx + " retry after 500: prevStatus=" + prevStatus);
            try
            {
                await Task.Delay(500, token);
            }
            catch (TaskCanceledException) { return resp; }

            try
            {
                var retried = await apiClient.GetEarningsSummaryAsync(token, start, end, granularity, null);
                return retried;
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Debug(LogTag + " AllTime chunk " + chunkIdx + "/" + chunkCount
                    + " attempt 2 threw: " + ex.Message);
                return resp; // fall back to first attempt's response (still failed)
            }
        }

        #endregion

        #region UI Updates

        private void UpdateUI()
        {
            TxtFollowers.Text = FormatCount(_totalFollowers);
            TxtSubscribers.Text = FormatCount(_totalSubscribers);
            // #U.2: show em-dash on the earnings cards when the fetch failed, distinguishing
            // "no data" (api error) from "actually $0".
            if (_lastFetchFailed)
            {
                TxtMonthEarnings.Text = "—";
                TxtTotalEarnings.Text = "—";
                TxtNetEarnings.Text = "Lifetime net: —";
            }
            else
            {
                TxtMonthEarnings.Text = FormatMoney(_monthGrossEarnings);
                TxtTotalEarnings.Text = FormatMoney(_totalGrossEarnings);
                TxtNetEarnings.Text = "Lifetime net: " + FormatMoney(_totalNetEarnings);
            }
            // Pie legend now populated dynamically by DrawPieChart based on selected mode (Net/Source/Type).

            // Per-card subtexts removed in favor of the dedicated Growth Tracker panel (#B).
            UpdateMonthChange();

            // Update recent followers/subscribers lists
            var allRecentFollowers = new List<RecentItem>();
            var allRecentSubscribers = new List<RecentItem>();
            var allRecentSpending = new List<RecentSpendingItem>();
            var allTopSpenders = new List<TopSpenderItem>();

            if (_selectedAccountKey == AllAccountsKey)
            {
                foreach (var acc in _accounts)
                {
                    allRecentFollowers.AddRange(acc.RecentFollowers.Select(f => new RecentItem
                    {
                        Username = f.Username + " (" + acc.Username + ")",
                        Time = f.Time
                    }));
                    allRecentSubscribers.AddRange(acc.RecentSubscribers.Select(s => new RecentItem
                    {
                        Username = s.Username + " (" + acc.Username + ")",
                        Time = s.Time
                    }));
                    allRecentSpending.AddRange(acc.RecentSpending.Select(r => new RecentSpendingItem
                    {
                        Date = r.Date,
                        Fan = r.Fan + " (" + acc.Username + ")",
                        Source = r.Source,
                        Gross = r.Gross
                    }));
                    allTopSpenders.AddRange(acc.TopSpenders.Select(t => new TopSpenderItem
                    {
                        Rank = t.Rank,
                        Fan = t.Fan + " (" + acc.Username + ")",
                        TotalGross = t.TotalGross
                    }));
                }
            }
            else
            {
                var account = _accounts.FirstOrDefault(x => x.Username == _selectedAccountKey);
                if (account != null)
                {
                    allRecentFollowers = account.RecentFollowers.ToList();
                    allRecentSubscribers = account.RecentSubscribers.ToList();
                    allRecentSpending = account.RecentSpending.ToList();
                    allTopSpenders = account.TopSpenders.ToList();
                }
            }

            // RecentFollowers panel removed from XAML 2026-04-28; data still aggregated for future use.
            RecentSubscribersList.ItemsSource = allRecentSubscribers.Take(10).ToList();
            InstallFilter(RecentSubscribersList, o =>
            {
                var item = o as RecentItem;
                return item != null && MatchesText(item.Username, _recentSubscribersSearch);
            });

            TxtNoSubscribers.Visibility = allRecentSubscribers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Recent spending: visible only on single-account view (inverse of AccountBreakdownPanel)
            RecentSpendingPanel.Visibility = _selectedAccountKey == AllAccountsKey ? Visibility.Collapsed : Visibility.Visible;
            var spendingDisplay = allRecentSpending.Take(20).ToList();
            RecentSpendingList.ItemsSource = spendingDisplay;
            InstallFilter(RecentSpendingList, o =>
            {
                var item = o as RecentSpendingItem;
                return item != null && MatchesText(item.Fan, _recentSpendingSearch);
            });
            TxtNoSpending.Visibility = spendingDisplay.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Top spenders: visible across both views
            var topSpendersDisplay = allTopSpenders
                .OrderByDescending(t => ParseMoney(t.TotalGross))
                .Take(10)
                .ToList();
            // Re-rank after aggregate sort
            for (int i = 0; i < topSpendersDisplay.Count; i++)
            {
                topSpendersDisplay[i].Rank = "#" + (i + 1);
            }
            TopSpendersList.ItemsSource = topSpendersDisplay;
            InstallFilter(TopSpendersList, o =>
            {
                var item = o as TopSpenderItem;
                return item != null && MatchesText(item.Fan, _topSpendersSearch);
            });
            TxtNoTopSpenders.Visibility = topSpendersDisplay.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // After both lists are populated, re-apply switcher visibility so only the active list's
            // scroll/search/empty-state is shown (the per-list visibility assignments above set both,
            // not yet aware of the merged-panel selection).
            ApplyBottomListVisibility();
        }

        private void UpdateMonthChange()
        {
            if (_prevMonthGrossEarnings <= 0)
            {
                TxtMonthChange.Text = "";
                TxtMonthChange.SetResourceReference(TextBlock.ForegroundProperty, "TextColorBrushAccordingTheme");
                return;
            }

            var diff = _monthGrossEarnings - _prevMonthGrossEarnings;
            var pct = (double)(diff / _prevMonthGrossEarnings) * 100.0;

            if (diff > 0)
            {
                TxtMonthChange.Text = "+" + pct.ToString("0.#") + "% vs last month";
                TxtMonthChange.SetResourceReference(TextBlock.ForegroundProperty, "GreenColorAccordingTheme");
            }
            else if (diff < 0)
            {
                TxtMonthChange.Text = pct.ToString("0.#") + "% vs last month";
                TxtMonthChange.SetResourceReference(TextBlock.ForegroundProperty, "TextColorBrushAccordingTheme");
            }
            else
            {
                TxtMonthChange.Text = "Flat vs last month";
                TxtMonthChange.SetResourceReference(TextBlock.ForegroundProperty, "TextColorBrushAccordingTheme");
            }
        }

        private decimal ParseMoney(string formatted)
        {
            if (string.IsNullOrEmpty(formatted)) return 0;
            var s = formatted.Replace("$", "").Replace(",", "").Trim();
            decimal multiplier = 1m;
            if (s.EndsWith("M"))
            {
                multiplier = 1000000m;
                s = s.Substring(0, s.Length - 1);
            }
            else if (s.EndsWith("k"))
            {
                multiplier = 1000m;
                s = s.Substring(0, s.Length - 1);
            }
            decimal value;
            if (decimal.TryParse(s, out value))
            {
                return value * multiplier;
            }
            return 0;
        }

        #endregion

        #region Chart Drawing

        private void DrawCharts()
        {
            DrawBarChart();
            DrawPieChart();
        }

        private void DrawBarChart()
        {
            EarningsChart.Children.Clear();

            // #V.4 — fetch-failed error feedback moved to BorderApiErrorBanner (top of tab). Charts
            // render whatever data is available (e.g., partial chunks succeeded). The U-era inline
            // overlay was removed; ShowApiErrorBanner / HideApiErrorBanner handle the error UI now.

            // Reconciliation diag (#A): every redraw logs the three range totals so we can see whether
            // bars vs source-breakdown vs type-breakdown agree within the SAME range. If they don't,
            // the discrepancy is either upstream from Fanvue or a UI dropping/aggregating bug.
            {
                decimal bucketsSum = _monthlyEarnings != null ? _monthlyEarnings.Values.Sum() : 0m;
                decimal sourcesSum = _breakdownBySource != null ? _breakdownBySource.Values.Sum() : 0m;
                decimal typesSum   = _earningsByType != null ? _earningsByType.Values.Sum() : 0m;
                GlobusLogHelper.log.Debug(LogTag + " RANGE-TOTALS: bucketsSumGross=$"
                    + bucketsSum.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + ", breakdownBySourceSumGross=$"
                    + sourcesSum.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + ", breakdownByTypeSumGross=$"
                    + typesSum.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }

            UpdateBarChartTitle();

            // Multi-series resolution (#C revised). Build per-series value arrays for each enabled
            // toggle. All series share the same bucketLabels x-axis from _monthlyEarnings keys.
            var bucketLabels = _monthlyEarnings.Keys.ToList();
            if (bucketLabels.Count == 0)
            {
                // Fall back to a single bucket so non-Earnings series still have an axis.
                bucketLabels.Add(DateTime.UtcNow.ToString(_currentBucketFormat ?? "MMM d"));
            }

            var enabledSeries = BuildEnabledSeries(bucketLabels);

            // Refresh the legend to mirror the enabled series (one swatch + label per active toggle).
            BuildEarningsSourceLegend();

            if (enabledSeries.Count == 0)
            {
                var msg = new TextBlock
                {
                    Text = "Select at least one series to display",
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B0B0B0")),
                    FontSize = 12
                };
                Canvas.SetLeft(msg, 10);
                Canvas.SetTop(msg, 90);
                EarningsChart.Children.Add(msg);
                GlobusLogHelper.log.Debug(LogTag + " DrawBarChart: no series enabled — empty chart");
                return;
            }

            var chartWidth = EarningsChart.ActualWidth > 0 ? EarningsChart.ActualWidth : 500;
            const double chartHeight = 180;

            // Y-axis auto-scale uses max abs value across ALL enabled series. When mixing dollars
            // and counts, this gives a unified spatial scale; per-bar/per-dot value labels remain
            // the truth for each series since they format with the series' own unit.
            decimal maxValue = 0m;
            foreach (var s in enabledSeries)
            {
                for (int i = 0; i < s.Values.Length; i++)
                {
                    decimal av = System.Math.Abs(s.Values[i]);
                    if (av > maxValue) { maxValue = av; }
                }
            }
            if (maxValue == 0) { maxValue = 1; }

            // Y-axis (#T point 4): 4 gridlines + $ value labels in the left margin. Dominant unit is
            // the unit of the series whose absolute max is largest; if all enabled are non-money the
            // axis labels render as integers.
            DrawYAxis(enabledSeries, maxValue, chartWidth, chartHeight);

            var barSlot = (chartWidth - 60) / bucketLabels.Count;
            if (barSlot < 6) { barSlot = 6; }
            // Mini-bars share a bucket slot. Reserve ~10px gutter, divide remainder by enabled count.
            int n = enabledSeries.Count;
            double miniWidth = (barSlot - 10) / n;
            if (miniWidth < 2) { miniWidth = 2; }

            if (_chartType == "Bars")
            {
                // Bars-mode label policy (#R + #T):
                //  - Drop labels entirely when (>2 series AND >12 buckets) — too cluttered (#R).
                //  - Drop labels also when (>=2 series AND >30 buckets) — readability overhaul (#T point 2).
                //  - Stagger vertical offset by series index (cy - 14 - sIdx*14) when >1 series.
                //  - Rotate -45° when miniBarWidth < 30 (>2 series) so labels don't collide horizontally.
                bool dropAllLabels = ((enabledSeries.Count > 2) && (bucketLabels.Count > 12))
                                  || ((enabledSeries.Count >= 2) && (bucketLabels.Count > 30));
                bool rotateLabels = (enabledSeries.Count > 2) && (miniWidth < 30);
                int staggerStep = enabledSeries.Count > 1 ? 14 : 0;
                if (dropAllLabels)
                {
                    GlobusLogHelper.log.Debug(LogTag + " Skipping per-bucket labels: too many series×buckets ("
                        + enabledSeries.Count + "×" + bucketLabels.Count + ")");
                }
                GlobusLogHelper.log.Debug(LogTag + " Value labels: barsCount=" + (bucketLabels.Count * enabledSeries.Count)
                    + " miniBarWidth=" + miniWidth.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " rotated=" + rotateLabels + " stagger=" + staggerStep + "px"
                    + " dropAll=" + dropAllLabels);

                // Grouped bar chart: each series gets its own mini-bar slot inside each bucket.
                for (int i = 0; i < bucketLabels.Count; i++)
                {
                    var slotLeft = 30.0 + i * barSlot;
                    for (int s = 0; s < enabledSeries.Count; s++)
                    {
                        var series = enabledSeries[s];
                        var value = series.Values[i];
                        var barHeight = (double)(System.Math.Abs(value) / maxValue) * (chartHeight - 40);
                        if (barHeight < 0) { barHeight = 0; }
                        if (barHeight < 2 && value != 0) { barHeight = 2; }

                        var rect = new Rectangle
                        {
                            Width = miniWidth,
                            Height = barHeight,
                            Fill = series.Brush,
                            RadiusX = 2,
                            RadiusY = 2
                        };
                        // Hover tooltip (#T point 3): always present so users can inspect even when
                        // value labels are dropped due to density.
                        rect.ToolTip = bucketLabels[i] + "\n" + series.Name + ": " + (series.IsMoney
                            ? FormatMoney(value)
                            : ((value > 0 ? "+" : "") + ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        double miniLeft = slotLeft + s * miniWidth;
                        Canvas.SetLeft(rect, miniLeft);
                        Canvas.SetTop(rect, chartHeight - 25 - barHeight);
                        EarningsChart.Children.Add(rect);

                        if (value != 0 && !dropAllLabels)
                        {
                            var valueLabel = new TextBlock
                            {
                                Text = series.IsMoney
                                    ? FormatMoney(value)
                                    : ((value > 0 ? "+" : "") + ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                FontSize = 9,
                                Foreground = new SolidColorBrush(series.Brush.Color)
                            };
                            // Stagger vertically by series index so labels from different series at the
                            // same bucket don't pile on top of each other (#R point 1).
                            double labelY = chartHeight - 25 - barHeight - 13 - (s * staggerStep);
                            Canvas.SetLeft(valueLabel, miniLeft);
                            Canvas.SetTop(valueLabel, labelY);
                            if (rotateLabels)
                            {
                                // Rotate -45° around top-left so labels don't overrun adjacent mini-bars (#R point 2).
                                valueLabel.RenderTransform = new System.Windows.Media.RotateTransform(-45);
                                valueLabel.RenderTransformOrigin = new System.Windows.Point(0, 0.5);
                            }
                            EarningsChart.Children.Add(valueLabel);
                        }
                    }
                }
            }
            else
            {
                // Line / Area: one Polyline (or Polygon) per enabled series in its own color.
                int seriesCount = enabledSeries.Count;
                for (int sIdx = 0; sIdx < enabledSeries.Count; sIdx++)
                {
                    var series = enabledSeries[sIdx];
                    var pointsX = new double[bucketLabels.Count];
                    var pointsY = new double[bucketLabels.Count];
                    for (int i = 0; i < bucketLabels.Count; i++)
                    {
                        var value = series.Values[i];
                        var barHeight = (double)(System.Math.Abs(value) / maxValue) * (chartHeight - 40);
                        if (barHeight < 0) { barHeight = 0; }
                        var slotLeft = 30.0 + i * barSlot;
                        pointsX[i] = slotLeft + barSlot / 2.0;
                        pointsY[i] = chartHeight - 25 - barHeight;
                    }
                    if (_chartType == "Area")
                    {
                        DrawAreaSeries(pointsX, pointsY, series.Values, series.Brush, chartHeight, series.IsMoney, sIdx, seriesCount, bucketLabels, series.Name);
                    }
                    else
                    {
                        DrawLineSeries(pointsX, pointsY, series.Values, series.Brush, chartHeight, series.IsMoney, sIdx, seriesCount, bucketLabels, series.Name);
                    }
                }
            }

            // 365-day SubscriberInsights truncation (#L): when the active range is longer than the
            // API's hard cap, draw a faint dashed vertical line at the boundary so users see which
            // portion of the "Subscribers Gained" series actually has data. Boundary is approximate —
            // walks bucketLabels and finds the first one >= UtcNow - 365d.
            if (_chartSubscriberRangeTruncated && _activeSeries == "SubscribersGained" && bucketLabels.Count > 0)
            {
                try
                {
                    var truncDate = DateTime.UtcNow.AddDays(-365);
                    int boundaryIdx = -1;
                    for (int i = 0; i < bucketLabels.Count; i++)
                    {
                        // Buckets are formatted strings; we use proportional position rather than date parse.
                        // Approximation: total span days * (i / count) compared to 365 from end.
                        // For simplicity render the line at chart position equivalent to last 365 days.
                        boundaryIdx = i;
                        break;
                    }
                    // Position the boundary line at chartWidth proportional to the last 365 days.
                    // bucketLabels span the full range; the boundary is at (totalDays - 365)/totalDays of the way across.
                    double truncTotalDays = (DateTime.UtcNow - (_selectedTimeRange ?? _timeRanges[0]).ComputeRange(DateTime.UtcNow).start).TotalDays;
                    if (truncTotalDays > 365)
                    {
                        double boundaryFrac = (truncTotalDays - 365.0) / truncTotalDays;
                        double bx = 30.0 + boundaryFrac * (chartWidth - 30);

                        var dash = new System.Windows.Shapes.Line
                        {
                            X1 = bx, Y1 = 0,
                            X2 = bx, Y2 = chartHeight - 25,
                            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B0B0B0")),
                            StrokeThickness = 1,
                            StrokeDashArray = new DoubleCollection(new double[] { 4, 3 })
                        };
                        EarningsChart.Children.Add(dash);

                        var note = new TextBlock
                        {
                            Text = "Subscribers data limited to last 365 days",
                            FontSize = 10,
                            FontStyle = FontStyles.Italic,
                            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B0B0B0"))
                        };
                        Canvas.SetLeft(note, bx + 4);
                        Canvas.SetTop(note, 4);
                        EarningsChart.Children.Add(note);
                    }
                }
                catch (Exception ex) { LogWarn("truncation marker render failed: " + ex.Message); }
            }

            // X-axis bucket labels — centralized renderer (#P) handles auto-rotate, year transitions,
            // density-based skipping, month gridlines, and first/last emphasis across all chart modes.
            // Build parallel bucketDates from the tab-level _bucketKeyToDate stash so the helper has
            // real DateTimes (no string-substring heuristics).
            var bucketDates = new List<DateTime>(bucketLabels.Count);
            for (int bi = 0; bi < bucketLabels.Count; bi++)
            {
                DateTime d;
                if (_bucketKeyToDate != null && _bucketKeyToDate.TryGetValue(bucketLabels[bi], out d))
                {
                    bucketDates.Add(d);
                }
                else
                {
                    bucketDates.Add(DateTime.MinValue); // sentinel: helper falls back gracefully
                }
            }
            double totalDays = (DateTime.UtcNow - (_selectedTimeRange ?? _timeRanges[0]).ComputeRange(DateTime.UtcNow).start).TotalDays;
            DrawXAxisLabels(bucketLabels, bucketDates, _currentGranularity, totalDays, chartWidth, chartHeight, barSlot);
        }

        // Shared X-axis renderer (#P). Emits one TextBlock per bucket label with these features:
        //  - Auto-rotate -45° when crowded (>12 buckets and per-slot width <50px).
        //  - Year-transition labels show year suffix even when general format is "MMM d".
        //  - Faint month dividers (daily granularity) or quarter ticks (weekly).
        //  - First and last labels bold + brighter (#E0E0E0) for range-endpoint emphasis.
        //  - Density skip when N>30 (every Nth label, always include first + last).
        //  - "MMM d, yy" for >365-day ranges (#S — chosen in FetchAccountData _currentBucketFormat).
        private void DrawXAxisLabels(List<string> bucketLabels, List<DateTime> bucketDates,
                                     string granularity, double totalDays,
                                     double chartWidth, double chartHeight, double barSlot)
        {
            if (bucketLabels == null || bucketLabels.Count == 0) { return; }
            int n = bucketLabels.Count;
            double perSlot = barSlot;
            // bucketDates is parallel to bucketLabels but may contain DateTime.MinValue sentinels
            // when a bucket key wasn't found in _bucketKeyToDate; year-transition + divider logic
            // gracefully treats sentinels as "unknown".
            bool haveDates = bucketDates != null && bucketDates.Count == n;

            // (a) Auto-rotate when crowded.
            bool rotate = (n > 12) && (perSlot < 50);
            // (e) Density skip when N>30.
            int step = 1;
            if (n > 30) { step = (int)System.Math.Ceiling(n / 25.0); if (step < 1) step = 1; }
            // (#T point 7) Tighter density floor for very dense ranges (~110 weekly buckets):
            // ensure step >= ceil(n/15) so we cap the visible label count near 15.
            int floorStep = (int)System.Math.Ceiling(n / 15.0);
            if (floorStep < 1) { floorStep = 1; }
            if (floorStep > step) { step = floorStep; }
            int rotation = rotate ? -45 : 0;
            string format = _currentBucketFormat ?? "MMM d";

            GlobusLogHelper.log.Debug(LogTag + " X-axis labels: bucketCount=" + n + " step=" + step
                + " rotation=" + rotation + "deg format=\"" + format + "\""
                + " totalDays=" + ((int)totalDays) + " haveDates=" + haveDates);

            bool formatHasYear = format.IndexOf("yy", StringComparison.OrdinalIgnoreCase) >= 0;

            // (c) Month dividers (daily) / quarter ticks (weekly) — now driven by real DateTimes.
            try
            {
                var gridStroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3D3D40")) { Opacity = 0.4 };
                gridStroke.Freeze();
                if (granularity == "day")
                {
                    int prevMonth = -1;
                    for (int i = 0; i < n; i++)
                    {
                        int month = -1;
                        if (haveDates && bucketDates[i] != DateTime.MinValue) { month = bucketDates[i].Month; }
                        else
                        {
                            // Fallback to the old leading-MMM heuristic when no DateTime is available.
                            string lbl = bucketLabels[i];
                            month = MonthIndex(lbl);
                        }
                        if (i > 0 && prevMonth >= 0 && month != prevMonth && month >= 0)
                        {
                            double gx = 30.0 + i * barSlot;
                            EarningsChart.Children.Add(new System.Windows.Shapes.Line
                            {
                                X1 = gx, Y1 = 0,
                                X2 = gx, Y2 = chartHeight - 25,
                                Stroke = gridStroke, StrokeThickness = 1
                            });
                        }
                        prevMonth = month;
                    }
                }
                else if (granularity == "week")
                {
                    // Real quarter boundaries: bucket where Date.Month is in {1,4,7,10} and crosses
                    // from a different quarter. Beats the old "every 13th bucket" approximation.
                    int prevQuarter = -1;
                    for (int i = 0; i < n; i++)
                    {
                        int q = -1;
                        if (haveDates && bucketDates[i] != DateTime.MinValue)
                        {
                            int mm = bucketDates[i].Month;
                            q = (mm - 1) / 3; // 0..3
                        }
                        if (i > 0 && prevQuarter >= 0 && q != prevQuarter && q >= 0)
                        {
                            double gx = 30.0 + i * barSlot;
                            EarningsChart.Children.Add(new System.Windows.Shapes.Line
                            {
                                X1 = gx, Y1 = chartHeight - 30,
                                X2 = gx, Y2 = chartHeight - 25,
                                Stroke = gridStroke, StrokeThickness = 1
                            });
                        }
                        prevQuarter = q;
                    }
                }
            }
            catch (Exception ex) { LogWarn("month/quarter gridline render failed: " + ex.Message); }

            // (d) + (e): First/last bold + brighter; skip non-first/last/every-Nth.
            for (int i = 0; i < n; i++)
            {
                bool isFirst = (i == 0);
                bool isLast  = (i == n - 1);
                bool include = isFirst || isLast || (i % step == 0);
                if (!include) { continue; }

                var label = bucketLabels[i];

                // (b) Year-transition: real DateTime comparison when available, fallback to month-wrap heuristic.
                string display = label;
                if (!formatHasYear && i > 0)
                {
                    bool yearChanged = false;
                    int yearForSuffix = DateTime.UtcNow.Year;
                    if (haveDates && bucketDates[i] != DateTime.MinValue && bucketDates[i - 1] != DateTime.MinValue)
                    {
                        if (bucketDates[i].Year != bucketDates[i - 1].Year)
                        {
                            yearChanged = true;
                            yearForSuffix = bucketDates[i].Year;
                        }
                    }
                    else
                    {
                        // Fallback heuristic for sentinel rows.
                        if (IsYearTransition(bucketLabels[i - 1], label)) { yearChanged = true; }
                    }
                    if (yearChanged)
                    {
                        display = label + ", " + (yearForSuffix % 100).ToString("D2");
                    }
                }

                var tb = new TextBlock
                {
                    Text = display,
                    FontSize = 11,
                    FontWeight = (isFirst || isLast) ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                        (isFirst || isLast) ? "#E0E0E0" : "#B0B0B0"))
                };
                double slotLeft = 30.0 + i * barSlot;
                double left = slotLeft + (barSlot - 30) / 2;

                if (rotate)
                {
                    // (a) RotateTransform per spec — origin at left-middle so the label hangs from the slot.
                    tb.RenderTransform = new System.Windows.Media.RotateTransform(-45);
                    tb.RenderTransformOrigin = new System.Windows.Point(0, 0.5);
                    Canvas.SetLeft(tb, slotLeft + barSlot / 2.0 - 4);
                    Canvas.SetTop(tb, chartHeight - 18);
                }
                else
                {
                    // (g) Fixed Y baseline below the chart, above the reconciliation row.
                    Canvas.SetLeft(tb, left);
                    Canvas.SetTop(tb, chartHeight - 20);
                }
                EarningsChart.Children.Add(tb);
            }
        }

        // Heuristic year-transition detector for "MMM d" labels. Returns true when previous label's
        // month-tag is later in the calendar than the current one (i.e., Dec -> Jan).
        private static bool IsYearTransition(string prevLabel, string curLabel)
        {
            if (string.IsNullOrEmpty(prevLabel) || string.IsNullOrEmpty(curLabel)) return false;
            int pIdx = MonthIndex(prevLabel);
            int cIdx = MonthIndex(curLabel);
            if (pIdx < 0 || cIdx < 0) return false;
            return pIdx > cIdx; // e.g., Dec(11) > Jan(0)
        }

        private static int MonthIndex(string label)
        {
            if (label == null || label.Length < 3) return -1;
            string m = label.Substring(0, 3);
            string[] months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
            for (int i = 0; i < months.Length; i++) { if (months[i].Equals(m, StringComparison.OrdinalIgnoreCase)) return i; }
            return -1;
        }

        // Series descriptor for the multi-series chart. IsMoney drives label format ($ vs +N).
        private class ChartSeries
        {
            public string Name;
            public decimal[] Values;
            public SolidColorBrush Brush;
            public bool IsMoney;
        }

        // #V.2 — single-active-series builder. Returns a one-element list with the series picked by
        // _activeSeries (mutex toggle group). All-Accounts view aggregates across accounts (sum of
        // per-account values); single-account view reads its dicts directly via the existing code
        // path (RefreshData has already aggregated/copied into _monthlyEarnings etc).
        private List<ChartSeries> BuildEnabledSeries(List<string> bucketLabels)
        {
            var list = new List<ChartSeries>();
            switch (_activeSeries)
            {
                case "EarningsNet":
                {
                    var values = new decimal[bucketLabels.Count];
                    for (int i = 0; i < bucketLabels.Count; i++)
                    {
                        decimal v;
                        if (_monthlyEarningsNet != null) { _monthlyEarningsNet.TryGetValue(bucketLabels[i], out v); values[i] = v; }
                    }
                    list.Add(new ChartSeries { Name = "Earnings (Net)", Values = values, Brush = MakeFrozenBrush("#2196F3"), IsMoney = true });
                    break;
                }
                case "FollowersGained":
                {
                    list.Add(new ChartSeries
                    {
                        Name = "Followers Gained",
                        Values = BuildFollowersGainedSeries(bucketLabels),
                        Brush = MakeFrozenBrush("#FF9800"),
                        IsMoney = false
                    });
                    break;
                }
                case "SubscribersGained":
                {
                    list.Add(new ChartSeries
                    {
                        Name = "Subscribers Gained",
                        Values = BuildSubscribersGainedSeries(bucketLabels),
                        Brush = MakeFrozenBrush("#E91E63"),
                        IsMoney = false
                    });
                    break;
                }
                case "EarningsGross":
                default:
                {
                    var values = new decimal[bucketLabels.Count];
                    for (int i = 0; i < bucketLabels.Count; i++)
                    {
                        decimal v;
                        if (_monthlyEarnings != null) { _monthlyEarnings.TryGetValue(bucketLabels[i], out v); values[i] = v; }
                    }
                    list.Add(new ChartSeries { Name = "Earnings (Gross)", Values = values, Brush = MakeFrozenBrush("#4CAF50"), IsMoney = true });
                    break;
                }
            }
            return list;
        }

        // #V.6 — BuildFollowersGainedSeriesForAccount, BuildSubscribersGainedSeriesForAccount, and
        // ShiftBrushForAccount were removed when single-series + Split-by-account toggle were deleted.

        private static SolidColorBrush MakeFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        // #Q: find the bucket whose [start, nextStart) range contains rowDate. Last bucket is open-ended.
        // Reads from the tab-level _bucketDates parallel list. Replaces the broken
        // `bucketLabels.IndexOf(rowDate.ToString(format))` which only matched when the row's day
        // stringified identically to the bucket-start day (rare for weekly buckets).
        // _bucketDates is built in RefreshData from
        // _monthlyEarnings.Keys -> _bucketKeyToDate, sorted ascending.
        private int FindBucketIndex(DateTime rowDate)
        {
            if (_bucketDates == null || _bucketDates.Count == 0) return -1;
            if (rowDate < _bucketDates[0]) return -1;
            for (int i = 0; i < _bucketDates.Count - 1; i++)
            {
                if (rowDate >= _bucketDates[i] && rowDate < _bucketDates[i + 1]) return i;
            }
            if (rowDate >= _bucketDates[_bucketDates.Count - 1]) return _bucketDates.Count - 1;
            return -1;
        }

        // Spec-shaped (#Q.4) variant — walks bucketLabels + _bucketKeyToDate directly. Identical
        // logic to the no-arg overload; kept available so callers that already have bucketLabels
        // in scope can use the spec-named helper without the indirection through _bucketDates.
        private int FindBucketIndexByDate(List<string> bucketLabels, DateTime rowDate)
        {
            if (_bucketKeyToDate == null || _bucketKeyToDate.Count == 0) return -1;
            int bestIdx = -1;
            DateTime bestDate = DateTime.MinValue;
            for (int i = 0; i < bucketLabels.Count; i++)
            {
                DateTime d;
                if (!_bucketKeyToDate.TryGetValue(bucketLabels[i], out d)) continue;
                if (d <= rowDate && d > bestDate)
                {
                    bestDate = d;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        // Per-bucket follower delta from snapshot history (#Q: date-range bucket matching).
        // Returns zeros when no usable history yet (DrawBarChart renders "Tracking started" then).
        private decimal[] BuildFollowersGainedSeries(List<string> bucketLabels)
        {
            var values = new decimal[bucketLabels.Count];
            IEnumerable<AccountData> targets;
            if (_selectedAccountKey == AllAccountsKey) { targets = _accounts; }
            else
            {
                var single = _accounts.FirstOrDefault(a => a.Username == _selectedAccountKey);
                targets = single != null ? new[] { single } : new AccountData[0];
            }

            int totalSnapshotRows = 0;
            int matchedRows = 0;
            int droppedRows = 0;
            decimal totalDelta = 0;
            var perAccountCounts = new List<string>();
            int bucketDatesCount = _bucketDates != null ? _bucketDates.Count : 0;

            foreach (var acc in targets)
            {
                if (string.IsNullOrEmpty(acc.Username)) { continue; }
                List<HistorySnapshot> rows;
                int rowCount = 0;
                if (_snapshotHistory.TryGetValue(acc.Username, out rows) && rows != null) { rowCount = rows.Count; }
                totalSnapshotRows += rowCount;
                perAccountCounts.Add(acc.Username + "=" + rowCount);

                if (rows == null || rows.Count < 2) { continue; }
                var sorted = rows.OrderBy(r => r.Date).ToList();
                for (int i = 1; i < sorted.Count; i++)
                {
                    int idx = FindBucketIndex(sorted[i].Date);
                    if (idx < 0) { droppedRows++; continue; }
                    decimal delta = sorted[i].Followers - sorted[i - 1].Followers;
                    values[idx] += delta;
                    matchedRows++;
                    totalDelta += delta;
                }
            }

            int nonZero = values.Count(v => v != 0);
            GlobusLogHelper.log.Debug(LogTag + " ChartSeries \"Followers Gained\" -> snapshotRowsTotal="
                + totalSnapshotRows + " (per account: " + string.Join(", ", perAccountCounts)
                + ") bucketDatesCount=" + bucketDatesCount
                + " matchedRows=" + matchedRows + " droppedRows=" + droppedRows
                + " nonZeroBuckets=" + nonZero + " totalDelta=" + totalDelta);
            return values;
        }

        // Per-bucket subscriber net delta (#Q: date-range bucket matching).
        // Each SubscriberInsights row.Date is a DAILY event date — buckets are typically WEEKLY so we
        // look up the row's containing bucket via FindBucketIndex, not string-equality on the label.
        private decimal[] BuildSubscribersGainedSeries(List<string> bucketLabels)
        {
            var values = new decimal[bucketLabels.Count];
            int sourceRows = _chartSubscriberInsightRows != null ? _chartSubscriberInsightRows.Count : 0;
            if (sourceRows == 0)
            {
                int mid = bucketLabels.Count / 2;
                if (mid < bucketLabels.Count) { values[mid] = _subscriberDelta; }
                GlobusLogHelper.log.Debug(LogTag + " ChartSeries \"Subscribers Gained\" -> bucketLabels="
                    + bucketLabels.Count + " sourceRows=0 — drew summary bar with delta=" + _subscriberDelta);
                return values;
            }
            int bucketDatesCount = _bucketDates != null ? _bucketDates.Count : 0;

            int matchedRows = 0;
            int droppedRows = 0;
            int totalDelta = 0;
            foreach (var row in _chartSubscriberInsightRows)
            {
                int idx = FindBucketIndex(row.Date);
                if (idx < 0) { droppedRows++; continue; }
                int delta = row.NewSubscribersCount - row.CancelledSubscribersCount;
                values[idx] += delta;
                matchedRows++;
                totalDelta += delta;
            }
            int nonZero = values.Count(v => v != 0);
            GlobusLogHelper.log.Debug(LogTag + " ChartSeries \"Subscribers Gained\" -> bucketLabels="
                + bucketLabels.Count + " bucketDatesCount=" + bucketDatesCount
                + " sourceRows=" + sourceRows + " matchedRows=" + matchedRows + " droppedRows=" + droppedRows
                + " nonZeroBuckets=" + nonZero + " totalDelta=" + totalDelta);
            return values;
        }

        // Y-axis gridlines + value labels (#T point 4). 4 horizontal gridlines at evenly spaced
        // y-positions across the chart plotting area. $ value labels (or integers for count series)
        // sit right-aligned in the 30px left margin. Dominant unit = the unit of the series with the
        // largest absolute max value. If no enabled series, the helper renders an empty axis frame.
        private void DrawYAxis(List<ChartSeries> enabledSeries, decimal maxValue, double chartWidth, double chartHeight)
        {
            if (chartWidth <= 60 || chartHeight <= 30) { return; }
            // Decide dominant unit (money vs count). Whichever series owns the largest |value|.
            bool dominantIsMoney = true;
            if (enabledSeries != null && enabledSeries.Count > 0)
            {
                decimal bestAbs = -1m;
                ChartSeries best = null;
                for (int s = 0; s < enabledSeries.Count; s++)
                {
                    var sv = enabledSeries[s];
                    if (sv == null || sv.Values == null) { continue; }
                    decimal localMax = 0m;
                    for (int i = 0; i < sv.Values.Length; i++)
                    {
                        decimal av = System.Math.Abs(sv.Values[i]);
                        if (av > localMax) { localMax = av; }
                    }
                    if (localMax > bestAbs) { bestAbs = localMax; best = sv; }
                }
                if (best != null) { dominantIsMoney = best.IsMoney; }
            }

            const int gridLineCount = 4; // 4 horizontal gridlines (top/quarters)
            double plotTop = 0.0;
            double plotBottom = chartHeight - 25;
            double plotLeft = 30.0;
            double plotRight = chartWidth - 30;
            if (plotRight <= plotLeft) { return; }

            var gridStroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3D3D40")) { Opacity = 0.3 };
            gridStroke.Freeze();
            var labelBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#888888"));
            labelBrush.Freeze();

            int labelsEmitted = 0;
            for (int g = 0; g <= gridLineCount; g++)
            {
                double frac = (double)g / gridLineCount;
                double y = plotBottom - frac * (plotBottom - plotTop);
                var line = new System.Windows.Shapes.Line
                {
                    X1 = plotLeft, Y1 = y, X2 = plotRight, Y2 = y,
                    Stroke = gridStroke,
                    StrokeThickness = 1.0
                };
                EarningsChart.Children.Add(line);

                decimal axisValue = maxValue * (decimal)frac;
                string axisText = dominantIsMoney
                    ? FormatMoney(axisValue)
                    : ((int)axisValue).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var lbl = new TextBlock
                {
                    Text = axisText,
                    FontSize = 10,
                    Foreground = labelBrush,
                    TextAlignment = TextAlignment.Right,
                    Width = 26
                };
                Canvas.SetLeft(lbl, 2);
                Canvas.SetTop(lbl, y - 7);
                EarningsChart.Children.Add(lbl);
                labelsEmitted++;
            }

            GlobusLogHelper.log.Debug(LogTag + " DrawYAxis: maxValue=" + maxValue.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " dominantIsMoney=" + dominantIsMoney + " gridlines=" + (gridLineCount + 1)
                + " labelsEmitted=" + labelsEmitted);
        }

        private void DrawBarSeries(List<string> bucketLabels, decimal[] values, double barSlot, double barWidth, SolidColorBrush brush, double chartHeight, decimal maxValue)
        {
            for (int i = 0; i < bucketLabels.Count; i++)
            {
                var value = values[i];
                var barHeight = (double)(value / maxValue) * (chartHeight - 40);
                if (barHeight < 2 && value > 0) { barHeight = 2; }

                var bar = new Rectangle
                {
                    Width = barWidth,
                    Height = barHeight,
                    Fill = brush,
                    RadiusX = 3,
                    RadiusY = 3
                };
                Canvas.SetLeft(bar, 30.0 + i * barSlot);
                Canvas.SetTop(bar, chartHeight - 25 - barHeight);
                EarningsChart.Children.Add(bar);
            }
        }

        // seriesIndex/seriesCount drive label staggering (#M) so labels from different series at the
        // same x don't overlap each other.
        // Label-density policy (#T point 1):
        //   bucketCount<=12   -> label every non-zero point
        //   13..40            -> label only local extrema
        //   >40               -> label ONLY global max + global min (2 labels per series)
        // Multi-series override (#T point 2): when seriesCount>=2 AND bucketCount>30, drop ALL labels.
        // Hover tooltip (#T point 3) is always attached to each dot.
        // Dot sizing (#T point 5): bucketCount<=12 -> 6px, default 5px, >40 -> 3px.
        // Dot thinning (#T point 6): when bucketCount>60, only emit dots at global max/min.
        // bucketLabels is passed only for tooltip text formatting.
        private void DrawLineSeries(double[] xs, double[] ys, decimal[] values, SolidColorBrush brush, double chartHeight, bool isMoney,
                                    int seriesIndex = 0, int seriesCount = 1, List<string> bucketLabels = null, string seriesName = null)
        {
            if (xs.Length < 2) { return; }
            var pts = new System.Windows.Media.PointCollection();
            for (int i = 0; i < xs.Length; i++) { pts.Add(new System.Windows.Point(xs[i], ys[i])); }
            var line = new System.Windows.Shapes.Polyline
            {
                Points = pts,
                Stroke = brush,
                StrokeThickness = 3.0,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            EarningsChart.Children.Add(line);

            DrawSeriesDotsAndLabels(xs, ys, values, brush, isMoney, seriesIndex, seriesCount, bucketLabels, seriesName);
        }

        private void DrawAreaSeries(double[] xs, double[] ys, decimal[] values, SolidColorBrush brush, double chartHeight, bool isMoney,
                                    int seriesIndex = 0, int seriesCount = 1, List<string> bucketLabels = null, string seriesName = null)
        {
            if (xs.Length < 2) { return; }
            var pts = new System.Windows.Media.PointCollection();
            double baseline = chartHeight - 25;
            pts.Add(new System.Windows.Point(xs[0], baseline));
            for (int i = 0; i < xs.Length; i++) { pts.Add(new System.Windows.Point(xs[i], ys[i])); }
            pts.Add(new System.Windows.Point(xs[xs.Length - 1], baseline));

            var fill = new SolidColorBrush(brush.Color) { Opacity = 0.30 };
            fill.Freeze();
            var poly = new System.Windows.Shapes.Polygon
            {
                Points = pts,
                Fill = fill,
                Stroke = brush,
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round
            };
            EarningsChart.Children.Add(poly);

            DrawSeriesDotsAndLabels(xs, ys, values, brush, isMoney, seriesIndex, seriesCount, bucketLabels, seriesName);
        }

        // Shared dot+label renderer for line/area (#T points 1,2,3,5,6). Centralized so the policy
        // is identical for both modes.
        private void DrawSeriesDotsAndLabels(double[] xs, double[] ys, decimal[] values, SolidColorBrush brush, bool isMoney,
                                             int seriesIndex, int seriesCount, List<string> bucketLabels, string seriesName)
        {
            int n = xs.Length;
            // Dot radius (#T point 5).
            double dotRadius = 5.0;
            if (n <= 12) { dotRadius = 6.0; }
            else if (n > 40) { dotRadius = 3.0; }

            // Multi-series + dense -> drop all labels (#T point 2).
            bool dropAllLabels = (seriesCount >= 2) && (n > 30);
            if (dropAllLabels)
            {
                GlobusLogHelper.log.Debug(LogTag + " Skipping value labels: dense multi-series ("
                    + seriesCount + " series x " + n + " buckets)");
            }
            // Per-bucket label mode (#T point 1).
            //   1 = label every nonzero
            //   2 = local extrema only
            //   3 = global max+min only
            int labelMode = 1;
            if (n > 40) { labelMode = 3; }
            else if (n > 12) { labelMode = 2; }
            // Spec-named aliases (#T): peakOnly = labelMode 2, globalOnly = labelMode 3.
            // Both are gated on dropAllLabels so a "skip all" wins.
            bool peakOnly   = !dropAllLabels && labelMode == 2;
            bool globalOnly = !dropAllLabels && labelMode == 3;

            int globalMaxIdx = -1, globalMinIdx = -1;
            if (values != null && values.Length > 0)
            {
                decimal vmax = decimal.MinValue, vmin = decimal.MaxValue;
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] > vmax) { vmax = values[i]; globalMaxIdx = i; }
                    if (values[i] < vmin) { vmin = values[i]; globalMinIdx = i; }
                }
            }

            // Dot thinning (#T point 6): >60 buckets -> only emit dots at global max/min.
            bool thinDots = n > 60;
            // Spec-named alias.
            bool denseDotsOnly = thinDots;

            int labelsEmitted = 0;
            for (int i = 0; i < n; i++)
            {
                bool isExtremum = (i == globalMaxIdx) || (i == globalMinIdx);
                bool drawDot = !denseDotsOnly || isExtremum;
                if (drawDot)
                {
                    var dot = new Ellipse { Width = dotRadius * 2, Height = dotRadius * 2, Fill = brush };
                    Canvas.SetLeft(dot, xs[i] - dotRadius);
                    Canvas.SetTop(dot, ys[i] - dotRadius);
                    if (values != null && i < values.Length && bucketLabels != null && i < bucketLabels.Count)
                    {
                        // Hover tooltip (#T point 3).
                        string tipVal = isMoney
                            ? FormatMoney(values[i])
                            : ((values[i] > 0 ? "+" : "") + ((int)values[i]).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        string namePart = string.IsNullOrEmpty(seriesName) ? "" : (seriesName + ": ");
                        dot.ToolTip = bucketLabels[i] + "\n" + namePart + tipVal;
                    }
                    EarningsChart.Children.Add(dot);
                }

                if (dropAllLabels) { continue; }
                if (values == null || i >= values.Length) { continue; }

                bool emitLabel;
                if (peakOnly)        { emitLabel = IsLocalExtremum(values, i); }
                else if (globalOnly) { emitLabel = (i == globalMaxIdx) || (i == globalMinIdx); }
                else                 { emitLabel = values[i] != 0; }
                if (!emitLabel) { continue; }

                DrawPointValueLabel(values[i], xs[i], ys[i], isMoney, seriesIndex, brush);
                labelsEmitted++;
            }

            GlobusLogHelper.log.Debug(LogTag + " DrawSeriesDotsAndLabels: series=\"" + (seriesName ?? "?") + "\""
                + " n=" + n + " dotRadius=" + dotRadius.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " labelMode=" + labelMode + " dropAll=" + dropAllLabels + " thinDots=" + thinDots
                + " labelsEmitted=" + labelsEmitted);
        }

        // Local maxima or minima — used by peak-only label mode (#M / #T). First/last points always
        // count so the user can read the start and end values. Strict inequalities for interior so a
        // flat run isn't flagged as an extremum at every point.
        private static bool IsLocalExtremum(decimal[] values, int i)
        {
            if (values == null || i < 0 || i >= values.Length) { return false; }
            if (i == 0 || i == values.Length - 1) { return true; }
            decimal prev = values[i - 1], cur = values[i], next = values[i + 1];
            return (cur > prev && cur > next) || (cur < prev && cur < next);
        }

        // Shared label renderer for line/area dots. Skips zero values. Staggers vertical offset by
        // seriesIndex so labels from different series don't pile on top of each other (#M).
        // Color matches the series brush so the user can tell which label belongs to which line.
        private void DrawPointValueLabel(decimal value, double cx, double cy, bool isMoney, int seriesIndex, SolidColorBrush seriesBrush)
        {
            if (value == 0) { return; }
            string text = isMoney
                ? FormatMoney(value)
                : ((value > 0 ? "+" : "") + ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var label = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = seriesBrush != null
                    ? new SolidColorBrush(seriesBrush.Color)
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0"))
            };
            Canvas.SetLeft(label, cx - 14);
            // Stagger vertical offset by series index — first series sits closest to dot, each subsequent
            // series steps up by 14px so labels never overlap at the same x position.
            Canvas.SetTop(label, cy - 18 - (seriesIndex * 14));
            EarningsChart.Children.Add(label);
        }

        private void ShowNoEarningsData()
        {
            var noDataText = new TextBlock
            {
                Text = "No earnings data",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B0B0B0")),
                FontSize = 12
            };
            Canvas.SetLeft(noDataText, 10);
            Canvas.SetTop(noDataText, 90);
            EarningsChart.Children.Add(noDataText);
        }

        // #V.4 — ShowFetchFailedState was removed; the API error banner at the top of the tab
        // (BorderApiErrorBanner) replaces the inline canvas overlay.

        private SolidColorBrush ResolveDominantSourceBrush()
        {
            if (_breakdownBySource == null || _breakdownBySource.Count == 0)
            {
                return PieSlicePaletteBrushes[0];
            }
            // Map each source (in pie's slice order: descending value) to its palette index;
            // dominant = first entry. This keeps bar color matching the largest pie slice for "BySource".
            int idx = 0;
            foreach (var kv in _breakdownBySource.OrderByDescending(k => k.Value))
            {
                if (kv.Value <= 0) { idx++; continue; }
                return PieSlicePaletteBrushes[idx % PieSlicePaletteBrushes.Length];
            }
            return PieSlicePaletteBrushes[0];
        }

        private void UpdateBarChartTitle()
        {
            if (TxtBarChartTitle == null) { return; }
            // Granularity prefix reflects the API's actual bucket size (day/week/month).
            string prefix;
            if (_currentGranularity == "day")        { prefix = "Daily"; }
            else if (_currentGranularity == "month") { prefix = "Monthly"; }
            else                                     { prefix = "Weekly"; }

            // Range name (#J): users were confused seeing "Weekly Earnings" while range="This Year".
            // Title now reads "{RangeLabel} — {GranularityPrefix} {SeriesNames}".
            string rangeLabel = _selectedTimeRange != null && !string.IsNullOrEmpty(_selectedTimeRange.Label)
                ? _selectedTimeRange.Label
                : "(default)";

            // #V.2 — single-series suffix.
            string seriesName;
            switch (_activeSeries)
            {
                case "EarningsNet":       seriesName = "Earnings (Net)"; break;
                case "FollowersGained":   seriesName = "Followers Gained"; break;
                case "SubscribersGained": seriesName = "Subscribers Gained"; break;
                case "EarningsGross":
                default:                  seriesName = "Earnings (Gross)"; break;
            }
            TxtBarChartTitle.Text = rangeLabel + " — " + prefix + " " + seriesName;
        }

        // Canonical source list — every known revenue source. Always rendered in the legend even at $0
        // so the user sees the full picture. Anything the API returns that isn't in this list is appended
        // (sorted by value) so we don't lose data if Fanvue adds a new source.
        private static readonly string[] KnownSources = new[]
        {
            "subs", "messages", "tips", "renewals", "referrals", "posts"
        };

        private void BuildEarningsSourceLegend()
        {
            if (EarningsSourceLegend == null) { return; }

            // Multi-series legend (#C revised): one row per ENABLED toggle, with the matching series
            // color swatch and the per-range total. Format follows the series' unit ($ vs +N).
            var items = new List<EarningsSourceLegendItem>();
            var bucketLabels = _monthlyEarnings != null && _monthlyEarnings.Count > 0
                ? _monthlyEarnings.Keys.ToList()
                : new List<string> { DateTime.UtcNow.ToString(_currentBucketFormat ?? "MMM d") };

            foreach (var s in BuildEnabledSeries(bucketLabels))
            {
                decimal total = 0;
                for (int i = 0; i < s.Values.Length; i++) { total += s.Values[i]; }
                string totalText = s.IsMoney
                    ? FormatMoney(total)
                    : ((total > 0 ? "+" : "") + ((int)total).ToString(System.Globalization.CultureInfo.InvariantCulture));
                items.Add(new EarningsSourceLegendItem
                {
                    Label = s.Name,
                    ValueText = totalText,
                    SwatchBrush = s.Brush
                });
            }

            EarningsSourceLegend.ItemsSource = items;

            UpdateChartReconciliationLabel();
        }

        private void UpdateChartReconciliationLabel()
        {
            if (TxtSourceReconciliation == null) { return; }
            decimal sourcesSum = _breakdownBySource != null ? _breakdownBySource.Values.Sum() : 0m;
            decimal barsSum = _monthlyEarnings != null ? _monthlyEarnings.Values.Sum() : 0m;
            decimal diff = System.Math.Abs(sourcesSum - barsSum);
            TxtSourceReconciliation.Text = "Sources total: " + FormatMoney(sourcesSum) + "   /   Bars total: " + FormatMoney(barsSum);
            try
            {
                if (diff > 1m)
                {
                    TxtSourceReconciliation.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336"));
                }
                else
                {
                    TxtSourceReconciliation.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B0B0B0"));
                }
            }
            catch { }
        }

        private List<string> BuildBucketLabels()
        {
            // Generate labels matching the buckets the server emits for the active range + granularity.
            // Uses DateTime.UtcNow to stay consistent with FetchAccountData range computation.
            var labels = new List<string>();
            var now = DateTime.UtcNow;
            var range = (_selectedTimeRange ?? _timeRanges[0]).ComputeRange(now);
            var rangeStart = range.start;
            var rangeEnd = range.end;
            var totalDays = (rangeEnd - rangeStart).TotalDays;
            var fmt = _currentBucketFormat ?? "MMM";

            if (totalDays <= 31)
            {
                // Daily buckets: one per day from rangeStart to rangeEnd (inclusive day boundaries).
                var startDay = new DateTime(rangeStart.Year, rangeStart.Month, rangeStart.Day, 0, 0, 0, DateTimeKind.Utc);
                var endDay = new DateTime(rangeEnd.Year, rangeEnd.Month, rangeEnd.Day, 0, 0, 0, DateTimeKind.Utc);
                for (var d = startDay; d <= endDay; d = d.AddDays(1))
                {
                    labels.Add(d.ToString(fmt));
                }
            }
            else if (totalDays <= 186)
            {
                // Weekly buckets: stride 7 days from rangeStart.
                var startDay = new DateTime(rangeStart.Year, rangeStart.Month, rangeStart.Day, 0, 0, 0, DateTimeKind.Utc);
                for (var d = startDay; d <= rangeEnd; d = d.AddDays(7))
                {
                    labels.Add(d.ToString(fmt));
                }
            }
            else
            {
                // Monthly buckets: first-of-month from start month to end month.
                var startMonth = new DateTime(rangeStart.Year, rangeStart.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                var endMonth = new DateTime(rangeEnd.Year, rangeEnd.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                for (var d = startMonth; d <= endMonth; d = d.AddMonths(1))
                {
                    labels.Add(d.ToString(fmt));
                }
            }

            if (labels.Count == 0) { labels.Add(now.ToString(fmt)); }
            return labels;
        }

        // Higher-contrast palette tuned for the #2D2D30 / #1E1E1E dark surface (2026-04-27 polish).
        // Saturated, well-separated hues so adjacent slices are clearly distinguishable.
        private static readonly string[] PieSlicePalette = new[]
        {
            "#4CAF50", // green
            "#2196F3", // blue
            "#FF9800", // orange
            "#E91E63", // pink
            "#9C27B0", // purple
            "#00BCD4", // cyan
            "#FFEB3B", // yellow
            "#F44336", // red
            "#607D8B", // blue-grey
            "#795548"  // brown (fallback)
        };

        // Cached SolidColorBrush instances for the palette so XAML legend bindings reuse them.
        private static readonly SolidColorBrush[] PieSlicePaletteBrushes = BuildPaletteBrushes();
        private static SolidColorBrush[] BuildPaletteBrushes()
        {
            var arr = new SolidColorBrush[PieSlicePalette.Length];
            for (int i = 0; i < PieSlicePalette.Length; i++)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(PieSlicePalette[i]));
                brush.Freeze();
                arr[i] = brush;
            }
            return arr;
        }

        private void DrawPieChart()
        {
            if (PieChart == null || _breakdownBySource == null || _earningsByType == null) return;
            PieChart.Children.Clear();
            if (PieLegend != null) { PieLegend.ItemsSource = null; }

            // #V.4 — pie does NOT short-circuit anymore; banner conveys the error state. Pie still
            // renders whatever it has (or empty-state) without dangling stale breakdowns.

            // Range-scope diag (#O): _breakdownBySource and _earningsByType come straight from
            // summaryResp.Data.BreakdownBySource/EarningsByType which the API returns scoped to
            // (startDate, endDate). Logging the per-redraw counts + sums confirms the pie reflects
            // the active CmbTimeRange selection, not lifetime data.
            GlobusLogHelper.log.Debug(LogTag + " Pie data range-scoped: source items=" + _breakdownBySource.Count
                + " type items=" + _earningsByType.Count
                + " sourceTotal=$" + _breakdownBySource.Values.Sum().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " typeTotal=$" + _earningsByType.Values.Sum().ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " mode=" + _pieMode + "/" + _pieValueMode);

            // Update the small "For: {RangeLabel} ({Net|Gross})" sub-header so users see range + value mode.
            if (TxtPieRangeLabel != null)
            {
                var label = _selectedTimeRange != null && !string.IsNullOrEmpty(_selectedTimeRange.Label)
                    ? _selectedTimeRange.Label
                    : "(default)";
                TxtPieRangeLabel.Text = "For: " + label + " (" + _pieValueMode + ")";
            }

            // Resolve which dictionary to read from based on (_pieMode, _pieValueMode) per #O.2.
            // Net is the default. Falls back gracefully when the chosen Net dict is empty.
            Dictionary<string, decimal> sliceSource;
            if (_pieMode == "ByType")
            {
                sliceSource = (_pieValueMode == "Gross") ? _earningsByType : _earningsByTypeNet;
            }
            else
            {
                sliceSource = (_pieValueMode == "Gross") ? _breakdownBySource : _breakdownBySourceNet;
            }
            // Defensive: if Net dict is unexpectedly empty but Gross has data (older fetch path), prefer
            // the populated one rather than show empty-state. Logged so the discrepancy is visible.
            if (sliceSource != null && sliceSource.Count == 0 && _pieValueMode == "Net")
            {
                Dictionary<string, decimal> grossFallback = (_pieMode == "ByType") ? _earningsByType : _breakdownBySource;
                if (grossFallback != null && grossFallback.Count > 0)
                {
                    GlobusLogHelper.log.Debug(LogTag + " Pie Net dict empty for mode " + _pieMode
                        + " — falling back to Gross dict so user sees data instead of empty pie");
                    sliceSource = grossFallback;
                }
            }

            // Build (label, value, hex, brush) slices.
            var slices = new List<(string label, decimal value, string hex, SolidColorBrush brush)>();
            string centerText = null;
            string centerColor = "#FFFFFF";
            if (sliceSource != null)
            {
                int idx = 0;
                foreach (var kv in sliceSource.OrderByDescending(k => k.Value))
                {
                    if (kv.Value <= 0) continue;
                    int p = idx % PieSlicePalette.Length;
                    slices.Add((kv.Key, kv.Value, PieSlicePalette[p], PieSlicePaletteBrushes[p]));
                    idx++;
                }
            }

            // Empty state — when the active range yields no slices, show "No earnings in this range"
            // centered inside the dim ring instead of an empty circle that looks broken (#O).
            decimal sliceTotal = slices.Sum(s => s.value);
            if (sliceTotal <= 0)
            {
                var emptyCircle = new Ellipse
                {
                    Width = 140,
                    Height = 140,
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
                    StrokeThickness = 20,
                    Fill = Brushes.Transparent
                };
                Canvas.SetLeft(emptyCircle, 5);
                Canvas.SetTop(emptyCircle, 5);
                PieChart.Children.Add(emptyCircle);

                var emptyText = new TextBlock
                {
                    Text = "No earnings in this range",
                    FontSize = 11,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B0B0B0")),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Width = 120
                };
                Canvas.SetLeft(emptyText, 15);
                Canvas.SetTop(emptyText, 65);
                PieChart.Children.Add(emptyText);

                if (PieLegend != null) { PieLegend.ItemsSource = new List<PieLegendItem>(); }
                GlobusLogHelper.log.Debug(LogTag + " DrawPieChart empty-state: no slices in active range "
                    + (_selectedTimeRange != null ? _selectedTimeRange.Label : "(default)"));
                return;
            }

            // Render slices
            const double centerX = 75.0;
            const double centerY = 75.0;
            const double radius = 60.0;
            double startAngle = 0;
            var legend = new List<PieLegendItem>();
            foreach (var s in slices)
            {
                double share = (double)(s.value / sliceTotal);
                double endAngle = startAngle + share * 360;
                var sliceColor = (Color)ColorConverter.ConvertFromString(s.hex);
                if (endAngle > startAngle + 0.001)
                {
                    var slice = CreatePieSlice(centerX, centerY, radius, startAngle, Math.Min(endAngle, 359.999), sliceColor);
                    PieChart.Children.Add(slice);
                }
                legend.Add(new PieLegendItem
                {
                    Label = ToTitleCase(s.label),
                    ValueText = FormatMoney(s.value),
                    Color = sliceColor,
                    SwatchBrush = s.brush
                });
                startAngle = endAngle;
            }

            // Donut hole
            var hole = new Ellipse
            {
                Width = 60,
                Height = 60,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D30"))
            };
            Canvas.SetLeft(hole, centerX - 30);
            Canvas.SetTop(hole, centerY - 30);
            PieChart.Children.Add(hole);

            // Optional center percentage (Net vs Fees mode)
            if (!string.IsNullOrEmpty(centerText))
            {
                var center = new TextBlock
                {
                    Text = centerText,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(centerColor))
                };
                Canvas.SetLeft(center, centerX - 15);
                Canvas.SetTop(center, centerY - 10);
                PieChart.Children.Add(center);
            }

            if (PieLegend != null) { PieLegend.ItemsSource = legend; }
        }

        private static string ToTitleCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + (s.Length > 1 ? s.Substring(1) : string.Empty);
        }

        public class PieLegendItem
        {
            public string Label { get; set; }
            public string ValueText { get; set; }
            public Color Color { get; set; }
            // Brush form for XAML binding — Color binding crashed XamlParser previously.
            public Brush SwatchBrush { get; set; }
        }

        public class EarningsSourceLegendItem
        {
            public string Label { get; set; }
            public string ValueText { get; set; }
            public Brush SwatchBrush { get; set; }
        }

        private System.Windows.Shapes.Path CreatePieSlice(double centerX, double centerY, double radius, double startAngle, double endAngle, Color color)
        {
            var startRad = (startAngle - 90) * Math.PI / 180;
            var endRad = (endAngle - 90) * Math.PI / 180;

            var x1 = centerX + radius * Math.Cos(startRad);
            var y1 = centerY + radius * Math.Sin(startRad);
            var x2 = centerX + radius * Math.Cos(endRad);
            var y2 = centerY + radius * Math.Sin(endRad);

            var largeArc = (endAngle - startAngle) > 180;

            var pathData = string.Format("M {0},{1} L {2},{3} A {4},{4} 0 {5} 1 {6},{7} Z",
                centerX, centerY, x1, y1, radius, largeArc ? 1 : 0, x2, y2);

            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(pathData),
                Fill = new SolidColorBrush(color)
            };
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
                    return value.ToString();
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
                if (diff.TotalMinutes < 60) return (int)diff.TotalMinutes + "m";
                if (diff.TotalHours < 24) return (int)diff.TotalHours + "h";
                if (diff.TotalDays < 7) return (int)diff.TotalDays + "d";
                return date.ToString("MMM d");
            }
            return "";
        }

        private string FormatCount(int count)
        {
            if (count >= 1000000) return (count / 1000000.0).ToString("0.#") + "M";
            if (count >= 1000) return (count / 1000.0).ToString("0.#") + "k";
            return count.ToString();
        }

        private string FormatMoney(decimal amount)
        {
            if (amount >= 1000000) return "$" + (amount / 1000000m).ToString("0.#") + "M";
            if (amount >= 1000) return "$" + (amount / 1000m).ToString("0.#") + "k";
            return "$" + amount.ToString("N0");
        }

        private void TxtTopSpendersSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _topSpendersSearch = TxtTopSpendersSearch != null ? (TxtTopSpendersSearch.Text ?? string.Empty) : string.Empty;
            RefreshView(TopSpendersList);
        }

        // Gross/Net sub-toggle for the pie chart (#O.2). Net is default. Mutually exclusive — when one
        // checks, the other unchecks. Triggers a redraw using the matching gross/net dictionary.
        private bool _pieValueSyncing;
        private void PieValue_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_pieValueSyncing) return;
            _pieValueSyncing = true;
            try
            {
                var clicked = sender as System.Windows.Controls.Primitives.ToggleButton;
                if (clicked == null) return;
                if (clicked != PieValueGross && PieValueGross != null) PieValueGross.IsChecked = false;
                if (clicked != PieValueNet   && PieValueNet   != null) PieValueNet.IsChecked = false;

                if (clicked == PieValueGross) { _pieValueMode = "Gross"; }
                else                          { _pieValueMode = "Net"; }

                GlobusLogHelper.log.Debug(LogTag + " PieValue_Checked -> _pieValueMode=" + _pieValueMode);
                DrawPieChart();
            }
            finally { _pieValueSyncing = false; }
        }

        private bool _pieToggleSyncing;
        private void PieMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (_pieToggleSyncing) return;
            _pieToggleSyncing = true;
            try
            {
                var clicked = sender as System.Windows.Controls.Primitives.ToggleButton;
                if (clicked == null) return;
                // Mutual exclusion: uncheck the other so the group behaves like a radio set.
                // (PieModeNetFees was removed in #O — toggle set is now BySource / ByType.)
                if (clicked != PieModeBySource && PieModeBySource != null) PieModeBySource.IsChecked = false;
                if (clicked != PieModeByType && PieModeByType != null) PieModeByType.IsChecked = false;

                if (clicked == PieModeByType) { _pieMode = "ByType"; }
                else                          { _pieMode = "BySource"; }

                DrawPieChart();
            }
            finally { _pieToggleSyncing = false; }
        }

        private void TxtRecentSubscribersSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _recentSubscribersSearch = TxtRecentSubscribersSearch != null ? (TxtRecentSubscribersSearch.Text ?? string.Empty) : string.Empty;
            RefreshView(RecentSubscribersList);
        }

        private void TxtRecentSpendingSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _recentSpendingSearch = TxtRecentSpendingSearch != null ? (TxtRecentSpendingSearch.Text ?? string.Empty) : string.Empty;
            RefreshView(RecentSpendingList);
        }

        private void TxtAccountBreakdownSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _accountBreakdownSearch = TxtAccountBreakdownSearch != null ? (TxtAccountBreakdownSearch.Text ?? string.Empty) : string.Empty;
            RefreshView(AccountBreakdownList);
        }

        private void RefreshView(ItemsControl control)
        {
            if (control == null || control.ItemsSource == null) { return; }
            var view = CollectionViewSource.GetDefaultView(control.ItemsSource);
            if (view != null) { view.Refresh(); }
        }

        private bool MatchesText(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) { return true; }
            if (string.IsNullOrEmpty(haystack)) { return false; }
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void InstallFilter(ItemsControl control, Predicate<object> predicate)
        {
            if (control == null || control.ItemsSource == null) { return; }
            var view = CollectionViewSource.GetDefaultView(control.ItemsSource);
            if (view != null) { view.Filter = predicate; }
        }

        private string GetLastSeenStorePath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = System.IO.Path.Combine(appDataPath, "Socinator1.0", "Fanvue");
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            return System.IO.Path.Combine(dir, "analytics-last-seen.json");
        }

        private void LoadLastSeenSnapshots()
        {
            try
            {
                var path = GetLastSeenStorePath();
                if (!File.Exists(path))
                {
                    _lastSeenSnapshots = new Dictionary<string, LastSeenSnapshot>();
                    return;
                }
                var json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, LastSeenSnapshot>>(json);
                _lastSeenSnapshots = loaded ?? new Dictionary<string, LastSeenSnapshot>();
                _tabLoadedAt = DateTime.UtcNow;
                StartMarkAsSeenTimer();
            }
            catch (Exception ex)
            {
                LogWarn("LoadLastSeenSnapshots failed: " + ex.Message);
                _lastSeenSnapshots = new Dictionary<string, LastSeenSnapshot>();
            }
        }

        private void SaveLastSeenSnapshots()
        {
            try
            {
                var path = GetLastSeenStorePath();
                var json = JsonConvert.SerializeObject(_lastSeenSnapshots, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                LogWarn("SaveLastSeenSnapshots failed: " + ex.Message);
            }
        }

        private string GetSnapshotHistoryPath()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = System.IO.Path.Combine(appDataPath, "Socinator1.0", "Fanvue");
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            return System.IO.Path.Combine(dir, "analytics-follower-history.json");
        }

        private void LoadSnapshotHistory()
        {
            try
            {
                var path = GetSnapshotHistoryPath();
                if (!File.Exists(path))
                {
                    _snapshotHistory = new Dictionary<string, List<HistorySnapshot>>();
                    return;
                }
                var json = File.ReadAllText(path);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, List<HistorySnapshot>>>(json);
                _snapshotHistory = loaded ?? new Dictionary<string, List<HistorySnapshot>>();
            }
            catch (Exception ex)
            {
                LogWarn("LoadSnapshotHistory failed: " + ex.Message);
                _snapshotHistory = new Dictionary<string, List<HistorySnapshot>>();
            }
        }

        private void SaveSnapshotHistory()
        {
            try
            {
                var path = GetSnapshotHistoryPath();
                var json = JsonConvert.SerializeObject(_snapshotHistory, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                LogWarn("SaveSnapshotHistory failed: " + ex.Message);
            }
        }

        // Upsert today's row for every loaded account so the Growth Tracker has data on every refresh.
        // Keyed by UTC midnight; same-day refresh overwrites the row instead of duplicating.
        private void UpsertTodaySnapshots()
        {
            try
            {
                var todayUtc = DateTime.UtcNow.Date;
                bool dirty = false;
                foreach (var acc in _accounts)
                {
                    if (string.IsNullOrEmpty(acc.Username)) { continue; }
                    List<HistorySnapshot> rows;
                    if (!_snapshotHistory.TryGetValue(acc.Username, out rows))
                    {
                        rows = new List<HistorySnapshot>();
                        _snapshotHistory[acc.Username] = rows;
                    }
                    var existing = rows.FirstOrDefault(r => r.Date.Date == todayUtc);
                    if (existing == null)
                    {
                        rows.Add(new HistorySnapshot { Date = todayUtc, Followers = acc.Followers, Subscribers = acc.Subscribers });
                        dirty = true;
                    }
                    else if (existing.Followers != acc.Followers || existing.Subscribers != acc.Subscribers)
                    {
                        existing.Followers = acc.Followers;
                        existing.Subscribers = acc.Subscribers;
                        dirty = true;
                    }
                }
                if (dirty) { SaveSnapshotHistory(); }
            }
            catch (Exception ex)
            {
                LogWarn("UpsertTodaySnapshots failed: " + ex.Message);
            }
        }

        // Returns the history row whose date is closest to (and not after) windowStart for this account.
        // null = no usable snapshot in window (caller treats as "tracking just started").
        private HistorySnapshot FindBaselineSnapshot(string username, DateTime windowStartUtc)
        {
            List<HistorySnapshot> rows;
            if (!_snapshotHistory.TryGetValue(username, out rows) || rows == null || rows.Count == 0) { return null; }
            HistorySnapshot best = null;
            foreach (var r in rows)
            {
                if (r.Date <= windowStartUtc)
                {
                    if (best == null || r.Date > best.Date) { best = r; }
                }
            }
            // If no snapshot is older than windowStart, fall back to the OLDEST available — that's
            // the earliest baseline we can offer (better than nothing for a brand-new tracker).
            if (best == null)
            {
                foreach (var r in rows)
                {
                    if (best == null || r.Date < best.Date) { best = r; }
                }
            }
            return best;
        }

        private async Task RefreshGrowthTrackerAsync()
        {
            if (TxtFollowersGained == null || TxtSubscribersGained == null) { return; }

            var nowUtc = DateTime.UtcNow;
            DateTime windowStartUtc;
            string windowLabel;
            if (_growthWindowDays <= 0)
            {
                // "All Time" — use earliest snapshot we have (or nowUtc if none).
                windowStartUtc = DateTime.MinValue;
                windowLabel = "all time";
            }
            else
            {
                windowStartUtc = nowUtc.AddDays(-_growthWindowDays);
                windowLabel = "last " + _growthWindowDays + " days";
            }

            // ---- Followers Gained from snapshot history ---------------------------------------
            int followersGained = 0;
            bool hasFollowerBaseline = false;
            DateTime followerBaselineDate = DateTime.MinValue;
            int currentFollowersTotal = 0;
            int baselineFollowersTotal = 0;

            IEnumerable<AccountData> targets;
            if (_selectedAccountKey == AllAccountsKey) { targets = _accounts; }
            else
            {
                var single = _accounts.FirstOrDefault(a => a.Username == _selectedAccountKey);
                targets = single != null ? new[] { single } : new AccountData[0];
            }

            foreach (var acc in targets)
            {
                currentFollowersTotal += acc.Followers;
                var baseline = FindBaselineSnapshot(acc.Username,
                    _growthWindowDays <= 0 ? DateTime.MinValue : windowStartUtc);
                if (baseline != null)
                {
                    baselineFollowersTotal += baseline.Followers;
                    hasFollowerBaseline = true;
                    if (followerBaselineDate == DateTime.MinValue || baseline.Date < followerBaselineDate)
                    {
                        followerBaselineDate = baseline.Date;
                    }
                }
            }

            if (hasFollowerBaseline)
            {
                followersGained = currentFollowersTotal - baselineFollowersTotal;
                TxtFollowersGained.Text = (followersGained >= 0 ? "+" : "") + followersGained;
                TxtGrowthFollowersSub.Text = "in " + windowLabel + " (since " + RelativeTimeString(followerBaselineDate) + ")";
            }
            else
            {
                TxtFollowersGained.Text = "0";
                TxtGrowthFollowersSub.Text = "tracking started — chart will populate over time";
            }

            // ---- Subscribers Gained via SubscriberInsights API for the window -----------------
            int subsGained = 0;
            int totalNew = 0;
            int totalCancelled = 0;
            try
            {
                // Cap to what the API supports (365 days max is the documented limit).
                var apiStart = _growthWindowDays <= 0 ? nowUtc.AddDays(-365) : windowStartUtc;
                var apiSize = (int)Math.Min(50, Math.Ceiling(((nowUtc - apiStart).TotalDays) + 1));
                if (apiSize < 1) { apiSize = 1; }

                GlobusLogHelper.log.Debug(LogTag + " GrowthTracker range \"" + windowLabel
                    + "\" -> start=" + apiStart.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    + " end=" + nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    + " size=" + apiSize);

                _lastSubscriberInsightRows.Clear();
                foreach (var acc in targets)
                {
                    if (acc.Credentials == null) { continue; }
                    var apiClient = new FanvueApiClient(_authService);
                    apiClient.Credentials = acc.Credentials;
                    var subUrl = "/insights/subscribers?startDate=" + apiStart.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        + "&endDate=" + nowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")
                        + "&size=" + apiSize;
                    GlobusLogHelper.log.Debug(LogTag + " GrowthTracker SubscriberInsights request -> " + subUrl + " (account=" + acc.Username + ")");
                    var resp = await apiClient.GetSubscriberInsightsAsync(System.Threading.CancellationToken.None, apiStart, nowUtc, apiSize, null);
                    int respStatus = (resp != null && resp.IsSuccess) ? 200 : 0;
                    int respCount = (resp != null && resp.Data != null && resp.Data.Data != null) ? resp.Data.Data.Count : 0;
                    GlobusLogHelper.log.Debug(LogTag + " GrowthTracker SubscriberInsights response status=" + respStatus + " eventCount=" + respCount + " (account=" + acc.Username + ")");
                    if (resp.IsSuccess && resp.Data != null && resp.Data.Data != null)
                    {
                        foreach (var row in resp.Data.Data)
                        {
                            totalNew += row.NewSubscribersCount;
                            totalCancelled += row.CancelledSubscribersCount;
                            _lastSubscriberInsightRows.Add(row);
                        }
                    }
                    else if (resp != null && !resp.IsSuccess)
                    {
                        var body = resp.ErrorMessage ?? "";
                        if (body.Length > 500) { body = body.Substring(0, 500); }
                        GlobusLogHelper.log.Debug(LogTag + " API failure code=" + respStatus + " body=" + body);
                    }
                }
                subsGained = totalNew - totalCancelled;
                TxtSubscribersGained.Text = (subsGained >= 0 ? "+" : "") + subsGained;
                TxtGrowthSubscribersSub.Text = "net new in " + windowLabel + " (" + totalNew + " new / " + totalCancelled + " cancelled)";
                GlobusLogHelper.log.Debug(LogTag + " GrowthTracker window=" + windowLabel + " followersGained=" + followersGained + " subsGained=" + subsGained + " (new=" + totalNew + ", cancelled=" + totalCancelled + ")");
            }
            catch (Exception ex)
            {
                LogWarn("RefreshGrowthTrackerAsync subs API failed: " + ex.Message);
                TxtSubscribersGained.Text = "—";
                TxtGrowthSubscribersSub.Text = "subs insights unavailable";
            }

            // If the active series depends on follower/subscriber data, redraw to pick up the freshly
            // cached _lastSubscriberInsightRows / snapshot history.
            if (_activeSeries == "FollowersGained" || _activeSeries == "SubscribersGained") { DrawBarChart(); }
        }

        private void StartMarkAsSeenTimer()
        {
            try
            {
                if (_markSeenTimer != null)
                {
                    _markSeenTimer.Stop();
                    _markSeenTimer.Tick -= MarkSeenTimer_Tick;
                }
                _markSeenTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(MarkSeenDwellSeconds)
                };
                _markSeenTimer.Tick += MarkSeenTimer_Tick;
                _markSeenTimer.Start();
            }
            catch (Exception ex)
            {
                LogWarn("StartMarkAsSeenTimer failed: " + ex.Message);
            }
        }

        private void MarkSeenTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                _markSeenTimer.Stop();
                MarkAccountsAsSeen();
            }
            catch (Exception ex)
            {
                LogWarn("MarkSeenTimer_Tick failed: " + ex.Message);
            }
        }

        private void MarkAccountsAsSeen()
        {
            if (_accounts == null || _accounts.Count == 0) { return; }
            var now = DateTime.UtcNow;
            foreach (var acc in _accounts)
            {
                if (string.IsNullOrEmpty(acc.Username)) { continue; }
                _lastSeenSnapshots[acc.Username] = new LastSeenSnapshot { Count = acc.Subscribers, Timestamp = now };
            }
            SaveLastSeenSnapshots();
            GlobusLogHelper.log.Debug(LogTag + " auto-marked " + _accounts.Count + " account(s) as seen");
        }

        private string RelativeTimeString(DateTime when)
        {
            if (when == default(DateTime)) { return "never"; }
            var diff = DateTime.UtcNow - when.ToUniversalTime();
            if (diff.TotalSeconds < 0) { return "just now"; }
            if (diff.TotalMinutes < 1) { return "just now"; }
            if (diff.TotalMinutes < 60) { return ((int)diff.TotalMinutes) + " min ago"; }
            if (diff.TotalHours < 24) { return ((int)diff.TotalHours) + " hours ago"; }
            if (diff.TotalDays < 2) { return "1 day ago"; }
            if (diff.TotalDays < 30) { return ((int)diff.TotalDays) + " days ago"; }
            if (diff.TotalDays < 60) { return "1 month ago"; }
            return ((int)(diff.TotalDays / 30)) + " months ago";
        }

        private void LogInfo(string msg) => GlobusLogHelper.log.Info(LogTag + " " + msg);
        private void LogWarn(string msg) => GlobusLogHelper.log.Warn(LogTag + " " + msg);
        private void LogError(string msg, Exception ex = null)
        {
            if (ex != null)
                GlobusLogHelper.log.Error(LogTag + " " + msg + " - " + ex.Message);
            else
                GlobusLogHelper.log.Error(LogTag + " " + msg);
        }

        #endregion

        #region Data Classes

        private class AccountData
        {
            public string Username { get; set; }
            public string Email { get; set; }
            public FanvueCredentials Credentials { get; set; }
            public DominatorAccountModel DominatorAccount { get; set; }
            public int Followers { get; set; }
            public int Subscribers { get; set; }
            public decimal TotalGross { get; set; }
            public decimal TotalNet { get; set; }
            public decimal MonthGross { get; set; }
            public decimal PrevMonthGross { get; set; }
            public int SubscriberDelta { get; set; }
            // Raw new/cancelled counts for the selected range (delta = New - Cancelled). Needed for export.
            public int NewSubscribersInRange { get; set; }
            public int CancelledSubscribersInRange { get; set; }
            // Net for this month (parallel to MonthGross/PrevMonthGross). Needed for export.
            public decimal MonthNet { get; set; }
            public Dictionary<string, decimal> MonthlyEarnings { get; set; } = new Dictionary<string, decimal>();
            // Parallel net buckets keyed identically to MonthlyEarnings — for export only (charts use gross).
            public Dictionary<string, decimal> MonthlyEarningsNet { get; set; } = new Dictionary<string, decimal>();
            // Source breakdown (e.g. subs/messages/posts/tips/renewals/...) — gross dollars per source.
            public Dictionary<string, decimal> BreakdownBySource { get; set; } = new Dictionary<string, decimal>();
            public Dictionary<string, decimal> BreakdownBySourceNet { get; set; } = new Dictionary<string, decimal>();
            // Type breakdown (e.g. renewals/messages/tips/subs) — gross dollars per type.
            public Dictionary<string, decimal> EarningsByType { get; set; } = new Dictionary<string, decimal>();
            public Dictionary<string, decimal> EarningsByTypeNet { get; set; } = new Dictionary<string, decimal>();
            // Per-account SubscriberInsights rows scoped to the CHART range (not the Growth Tracker
            // window). Populated by FetchAccountData; aggregated into _chartSubscriberInsightRows in
            // RefreshData; consumed by BuildSubscribersGainedSeries (#L).
            public List<SubscriberEventRowDto> SubscriberInsightRows { get; set; } = new List<SubscriberEventRowDto>();
            // Parallel bucket-key -> bucket-DateTime so the X-axis renderer (#P) has real DateTimes
            // for year-transition + month/quarter divider detection without parsing label strings.
            public Dictionary<string, DateTime> BucketKeyToDate { get; set; } = new Dictionary<string, DateTime>();
            // Set true when ALL EarningsSummary chunks failed for this account in the latest fetch
            // (#U.2). Used by RefreshData to flip the tab-level _lastFetchFailed flag so the chart shows
            // an error state instead of stale source/type breakdowns from a prior successful fetch.
            public bool EarningsFetchFailed { get; set; }
            public List<RecentItem> RecentFollowers { get; set; } = new List<RecentItem>();
            public List<RecentItem> RecentSubscribers { get; set; } = new List<RecentItem>();
            public List<RecentSpendingItem> RecentSpending { get; set; } = new List<RecentSpendingItem>();
            public List<TopSpenderItem> TopSpenders { get; set; } = new List<TopSpenderItem>();
        }

        public class RecentItem
        {
            public string Username { get; set; }
            public string Time { get; set; }
        }

        // #V.3 — analytics-cache.json schema. Whole-tab snapshot persisted on every successful refresh.
        public class AnalyticsCacheDto
        {
            public DateTime SavedAt { get; set; }
            public string RangeLabel { get; set; }
            public List<AnalyticsCacheAccountDto> Accounts { get; set; }
        }

        public class AnalyticsCacheAccountDto
        {
            public string Username { get; set; }
            public int Followers { get; set; }
            public int Subscribers { get; set; }
            public decimal TotalGross { get; set; }
            public decimal TotalNet { get; set; }
            public decimal MonthGross { get; set; }
            public decimal MonthNet { get; set; }
            public Dictionary<string, decimal> MonthlyEarnings { get; set; }
            public Dictionary<string, decimal> MonthlyEarningsNet { get; set; }
            public Dictionary<string, decimal> BreakdownBySource { get; set; }
            public Dictionary<string, decimal> BreakdownBySourceNet { get; set; }
            public Dictionary<string, decimal> EarningsByType { get; set; }
            public Dictionary<string, decimal> EarningsByTypeNet { get; set; }
            public Dictionary<string, DateTime> BucketKeyToDate { get; set; }
        }

        public class AccountBreakdownItem
        {
            public string Username { get; set; }
            public string Followers { get; set; }
            public string Subscribers { get; set; }
            public string MonthEarnings { get; set; }
            public string TotalEarnings { get; set; }
            public SolidColorBrush StatusColor { get; set; }
        }

        public class RecentSpendingItem
        {
            public string Date { get; set; }
            public string Fan { get; set; }
            public string Source { get; set; }
            public string Gross { get; set; }
        }

        public class TopSpenderItem
        {
            public string Rank { get; set; }
            public string Fan { get; set; }
            public string TotalGross { get; set; }
        }

        private class TimeRangeOption
        {
            public string Label { get; set; }
            public Func<DateTime, (DateTime start, DateTime end)> ComputeRange { get; set; }
        }

        public class LastSeenSnapshot
        {
            [JsonProperty("count")]
            public int Count { get; set; }

            [JsonProperty("timestamp")]
            public DateTime Timestamp { get; set; }
        }

        // Daily snapshot row used to compute follower/subscriber growth over arbitrary windows.
        // One entry per UTC day per account. Older entries are kept indefinitely so deeper history
        // (1 year, all-time) becomes meaningful as the app runs longer.
        public class HistorySnapshot
        {
            [JsonProperty("date")]
            public DateTime Date { get; set; }   // midnight UTC of the snapshot day
            [JsonProperty("followers")]
            public int Followers { get; set; }
            [JsonProperty("subscribers")]
            public int Subscribers { get; set; }
        }

        #endregion
    }
}
