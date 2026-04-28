using DominatorHouseCore.Enums;
using DominatorHouseCore.FileManagers;
using DominatorHouseCore.LogHelper;
using DominatorHouseCore.Models;
using DominatorHouseCore.Utility;
using DominatorUIUtility.ViewModel;
using BindableBase = Prism.Mvvm.BindableBase;
using FanvueDominatorCore.Models;
using FanvueDominatorCore.Models.Dtos;
using FanvueDominatorCore.Services;
using FanvueDominatorUI.ViewModels.Settings;
using FanvueDominatorUI.Views.Notifications;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FanvueDominatorUI.ViewModels.Engage
{
    public class ChatConversationItem : BindableBase
    {
        private string _userUuid;
        private string _handle;
        private string _displayName;
        private string _nickname;
        private string _avatarUrl;
        private string _lastMessagePreview;
        private DateTime? _lastMessageAt;
        private int _unreadCount;
        private bool _isOnline;
        private DateTime? _lastSeenAt;
        private bool _isTopSpender;
        private bool _isRead;
        private bool _isMuted;
        private DateTime? _registeredAt;

        public string UserUuid { get { return _userUuid; } set { SetProperty(ref _userUuid, value); } }
        public string Handle { get { return _handle; } set { SetProperty(ref _handle, value); } }
        public string DisplayName { get { return _displayName; } set { SetProperty(ref _displayName, value); } }
        public string Nickname { get { return _nickname; } set { SetProperty(ref _nickname, value); } }
        public string AvatarUrl { get { return _avatarUrl; } set { SetProperty(ref _avatarUrl, value); } }
        public string LastMessagePreview { get { return _lastMessagePreview; } set { SetProperty(ref _lastMessagePreview, value); } }
        public DateTime? LastMessageAt { get { return _lastMessageAt; } set { SetProperty(ref _lastMessageAt, value); } }
        public int UnreadCount { get { return _unreadCount; } set { SetProperty(ref _unreadCount, value); } }
        public bool IsOnline { get { return _isOnline; } set { SetProperty(ref _isOnline, value); } }
        public DateTime? LastSeenAt { get { return _lastSeenAt; } set { SetProperty(ref _lastSeenAt, value); } }
        public bool IsTopSpender { get { return _isTopSpender; } set { SetProperty(ref _isTopSpender, value); } }
        public bool IsRead { get { return _isRead; } set { SetProperty(ref _isRead, value); } }
        public bool IsMuted { get { return _isMuted; } set { SetProperty(ref _isMuted, value); } }
        public DateTime? RegisteredAt { get { return _registeredAt; } set { SetProperty(ref _registeredAt, value); } }
    }

    public class ChatThreadMessage : BindableBase
    {
        public string Uuid { get; set; }
        public string Text { get; set; }
        public DateTime? SentAt { get; set; }
        public bool IsOutgoing { get; set; }
        public bool HasMedia { get; set; }
        public string MediaType { get; set; }
        public string SenderUuid { get; set; }
        public long? Price { get; set; }
        public List<string> MediaUuids { get; set; }
        public bool IsPaid { get { return Price.HasValue && Price.Value > 0; } }
        public string PriceDisplay { get { return Price.HasValue && Price.Value > 0 ? "$" + (Price.Value / 100m).ToString("0.00") : string.Empty; } }
    }

    public class ChatThreadMedia : BindableBase
    {
        public string Uuid { get; set; }
        public string MediaType { get; set; }
        public string Url { get; set; }
        public string ThumbnailUrl { get; set; }
        public DateTime? CreatedAt { get; set; }
    }

    public class FanvueAccountOption : BindableBase
    {
        public string Username { get; set; }
        public string Email { get; set; }
        public FanvueCredentials Credentials { get; set; }
        public string Display { get { return "@" + (Username ?? string.Empty); } }
    }

    public class ChatFilterChip : BindableBase
    {
        private bool _isActive;
        public string Label { get; set; }
        public string ApiValue { get; set; }
        public bool IsActive { get { return _isActive; } set { SetProperty(ref _isActive, value); } }
    }

    public class ChatMonitorViewModel : BindableBase
    {
        private const string LogTag = "[FanvueChatMonitor]";

        private readonly FanvueAuthService _authService = new FanvueAuthService();
        private CancellationTokenSource _cts;
        private DispatcherTimer _searchDebounce;
        // Slice F Option A: poller is owned by ChatMonitorViewModel — re-created on
        // SelectedAccount change. Notifications fire only while this tab is alive;
        // cross-tab notifications are deferred to a future slice.
        private FanvueNotificationPoller _poller;

        private ObservableCollection<FanvueAccountOption> _accounts = new ObservableCollection<FanvueAccountOption>();
        public ObservableCollection<FanvueAccountOption> Accounts { get { return _accounts; } }

        private FanvueAccountOption _selectedAccount;
        public FanvueAccountOption SelectedAccount
        {
            get { return _selectedAccount; }
            set
            {
                if (SetProperty(ref _selectedAccount, value))
                {
                    LoadConversationsCommand.RaiseCanExecuteChanged();
                    if (SendMessageCommand != null) { SendMessageCommand.RaiseCanExecuteChanged(); }
                    RestartPoller();
                    if (value != null)
                    {
                        _ = LoadConversationsAsync();
                    }
                }
            }
        }

        private ObservableCollection<ChatConversationItem> _conversations = new ObservableCollection<ChatConversationItem>();
        public ObservableCollection<ChatConversationItem> Conversations { get { return _conversations; } }

        private ChatConversationItem _selectedConversation;
        public ChatConversationItem SelectedConversation
        {
            get { return _selectedConversation; }
            set
            {
                if (SetProperty(ref _selectedConversation, value))
                {
                    if (SendMessageCommand != null) { SendMessageCommand.RaiseCanExecuteChanged(); }
                    if (value != null)
                    {
                        _ = LoadThreadAsync(value);
                    }
                }
            }
        }

        private ObservableCollection<ChatThreadMessage> _messages = new ObservableCollection<ChatThreadMessage>();
        public ObservableCollection<ChatThreadMessage> Messages { get { return _messages; } }

        private ObservableCollection<ChatThreadMedia> _media = new ObservableCollection<ChatThreadMedia>();
        public ObservableCollection<ChatThreadMedia> Media { get { return _media; } }

        private int _unreadChats;
        public int UnreadChats { get { return _unreadChats; } set { SetProperty(ref _unreadChats, value); } }

        private int _unreadMessages;
        public int UnreadMessages { get { return _unreadMessages; } set { SetProperty(ref _unreadMessages, value); } }

        private bool _isBusy;
        public bool IsBusy { get { return _isBusy; } set { SetProperty(ref _isBusy, value); } }

        private string _statusMessage;
        public string StatusMessage { get { return _statusMessage; } set { SetProperty(ref _statusMessage, value); } }

        private ObservableCollection<ChatFilterChip> _filterChips = new ObservableCollection<ChatFilterChip>();
        public ObservableCollection<ChatFilterChip> FilterChips { get { return _filterChips; } }

        private string _searchText;
        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    RestartSearchDebounce();
                }
            }
        }

        private string _composerText;
        public string ComposerText
        {
            get { return _composerText; }
            set
            {
                if (SetProperty(ref _composerText, value))
                {
                    SendMessageCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isSending;
        public bool IsSending
        {
            get { return _isSending; }
            set
            {
                if (SetProperty(ref _isSending, value))
                {
                    SendMessageCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public DelegateCommand RefreshAccountsCommand { get; }
        public DelegateCommand LoadConversationsCommand { get; }
        public DelegateCommand<ChatConversationItem> MarkReadCommand { get; }
        public DelegateCommand<ChatConversationItem> ToggleMuteCommand { get; }
        public DelegateCommand<ChatFilterChip> ToggleFilterCommand { get; }
        public DelegateCommand SendMessageCommand { get; }

        public ChatMonitorViewModel()
        {
            RefreshAccountsCommand = new DelegateCommand(LoadAccounts);
            LoadConversationsCommand = new DelegateCommand(async () => await LoadConversationsAsync(), () => SelectedAccount != null);
            MarkReadCommand = new DelegateCommand<ChatConversationItem>(async c => await UpdateChatAsync(c, isRead: true, isMuted: null));
            ToggleMuteCommand = new DelegateCommand<ChatConversationItem>(async c => await UpdateChatAsync(c, isRead: null, isMuted: c != null ? (bool?)true : null));
            ToggleFilterCommand = new DelegateCommand<ChatFilterChip>(OnToggleFilter);
            SendMessageCommand = new DelegateCommand(async () => await SendMessageAsync(), CanSendMessage);

            // Filter chips: All / Unread / Top Spenders / Subscribers / Tippers / Online.
            // "All" carries a null ApiValue and clears the others when activated.
            _filterChips.Add(new ChatFilterChip { Label = "All", ApiValue = null, IsActive = true });
            _filterChips.Add(new ChatFilterChip { Label = "Unread", ApiValue = "unread" });
            _filterChips.Add(new ChatFilterChip { Label = "Top Spenders", ApiValue = "spenders" });
            _filterChips.Add(new ChatFilterChip { Label = "Subscribers", ApiValue = "subscribers" });
            _filterChips.Add(new ChatFilterChip { Label = "Tippers", ApiValue = "has_tipped" });
            _filterChips.Add(new ChatFilterChip { Label = "Online", ApiValue = "online" });

            _authService.CredentialsUpdated += OnCredentialsUpdated;

            LoadAccounts();
        }

        private void OnToggleFilter(ChatFilterChip chip)
        {
            if (chip == null) { return; }

            if (chip.ApiValue == null)
            {
                // "All" clears every other chip.
                foreach (var c in _filterChips)
                {
                    c.IsActive = (c == chip);
                }
            }
            else
            {
                // Toggling any non-All chip turns "All" off if at least one filter is active.
                bool anyActive = false;
                foreach (var c in _filterChips)
                {
                    if (c.ApiValue != null && c.IsActive) { anyActive = true; break; }
                }
                var allChip = _filterChips.FirstOrDefault(c => c.ApiValue == null);
                if (allChip != null) { allChip.IsActive = !anyActive; }
            }

            if (SelectedAccount != null) { _ = LoadConversationsAsync(); }
        }

        private void RestartSearchDebounce()
        {
            try
            {
                if (_searchDebounce == null)
                {
                    _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                    _searchDebounce.Tick += OnSearchDebounceTick;
                }
                _searchDebounce.Stop();
                _searchDebounce.Start();
            }
            catch { }
        }

        private void OnSearchDebounceTick(object sender, EventArgs e)
        {
            try
            {
                if (_searchDebounce != null) { _searchDebounce.Stop(); }
                if (SelectedAccount != null) { _ = LoadConversationsAsync(); }
            }
            catch { }
        }

        private List<string> GetActiveFilterValues()
        {
            var list = new List<string>();
            foreach (var c in _filterChips)
            {
                if (c.IsActive && !string.IsNullOrEmpty(c.ApiValue))
                {
                    list.Add(c.ApiValue);
                }
            }
            return list;
        }

        public void Cancel()
        {
            try
            {
                CancelInFlight();
                if (_searchDebounce != null)
                {
                    _searchDebounce.Stop();
                    _searchDebounce.Tick -= OnSearchDebounceTick;
                    _searchDebounce = null;
                }
                StopAndDisposePoller();
                _authService.CredentialsUpdated -= OnCredentialsUpdated;
            }
            catch { }
        }

        private void RestartPoller()
        {
            try
            {
                StopAndDisposePoller();
                if (_selectedAccount == null || _selectedAccount.Credentials == null) { return; }

                var settings = NotificationSettingsViewModel.LoadFromDisk();
                _poller = new FanvueNotificationPoller(_authService, _selectedAccount.Credentials, settings);
                _poller.NotificationFired += OnNotificationFired;
                _poller.Start();
                // #W.UI — apply the latest visibility state so a tab-already-visible scenario
                // doesn't get stuck at the 60s default.
                _poller.SetVisibilityHint(_lastTabVisible, _lastAppActive);
                RaisePollerReplaced();
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Warn(LogTag + " RestartPoller failed: " + ex.Message);
            }
        }

        private void StopAndDisposePoller()
        {
            try
            {
                if (_poller != null)
                {
                    _poller.NotificationFired -= OnNotificationFired;
                    _poller.Dispose();
                    _poller = null;
                    RaisePollerReplaced();
                }
            }
            catch { }
        }

        private void OnNotificationFired(object sender, FanvueNotificationEvent e)
        {
            try
            {
                if (e == null) { return; }

                // #W.7 — when a NewMessage carries per-message detail (ChatId set), surface the rich
                // toast: title = "{senderDisplayName} sent a message", body = preview, click navigates
                // to the conversation. Falls back to count-only summary for everything else.
                if (e.Type == FanvueNotificationType.NewMessage && !string.IsNullOrEmpty(e.ChatId))
                {
                    string senderName = !string.IsNullOrEmpty(e.SenderDisplayName) ? e.SenderDisplayName
                                  : !string.IsNullOrEmpty(e.SenderHandle) ? e.SenderHandle
                                  : "Someone";
                    string richTitle = senderName + " sent a message";
                    string richBody = string.IsNullOrEmpty(e.MessagePreview) ? (e.Summary ?? string.Empty) : e.MessagePreview;
                    string targetChatId = e.ChatId;
                    NotificationToast.Show(richTitle, richBody, () => NavigateToConversation(targetChatId));
                    return;
                }

                string title;
                switch (e.Type)
                {
                    case FanvueNotificationType.NewMessage: title = "New message"; break;
                    case FanvueNotificationType.NewSubscriber: title = "New subscriber"; break;
                    case FanvueNotificationType.NewFollower: title = "New follower"; break;
                    case FanvueNotificationType.NewTip: title = "New tip"; break;
                    default: title = "Fanvue"; break;
                }
                string body = (e.AccountUsername != null ? "@" + e.AccountUsername + " — " : string.Empty) + (e.Summary ?? string.Empty);
                NotificationToast.Show(title, body);
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Warn(LogTag + " OnNotificationFired failed: " + ex.Message);
            }
        }

        // #W.7 — toast click navigation. Find the conversation matching chatId (== other-user UUID)
        // in the current list and assign it as SelectedConversation. If not in the list yet (chat
        // arrived from outside the loaded subset), trigger a list reload first.
        private void NavigateToConversation(string chatId)
        {
            try
            {
                if (string.IsNullOrEmpty(chatId)) { return; }
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var match = _conversations.FirstOrDefault(c => c.UserUuid == chatId);
                        if (match != null)
                        {
                            SelectedConversation = match;
                            GlobusLogHelper.log.Debug(LogTag + " NavigateToConversation: selected existing chat=" + chatId);
                        }
                        else
                        {
                            GlobusLogHelper.log.Debug(LogTag + " NavigateToConversation: chat=" + chatId + " not in current list, reloading");
                            _ = LoadConversationsAsync();
                        }
                    }
                    catch (Exception innerEx)
                    {
                        GlobusLogHelper.log.Warn(LogTag + " NavigateToConversation dispatch body failed: " + innerEx.Message);
                    }
                }));
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Warn(LogTag + " NavigateToConversation failed: " + ex.Message);
            }
        }

        // #W.UI — host calls these when the tab visibility or app foreground state changes so the
        // poller can switch between 10s (foreground+visible) and 60s (hidden/minimized) cadence.
        // Both states are cached so single-axis transitions compose with the latest other axis.
        private bool _lastTabVisible = true;
        private bool _lastAppActive = true;

        public void NotifyTabVisible(bool visible)
        {
            _lastTabVisible = visible;
            GlobusLogHelper.log.Debug(LogTag + " Visibility hint: tabVisible=" + _lastTabVisible + " appActive=" + _lastAppActive);
            if (_poller != null) { _poller.SetVisibilityHint(_lastTabVisible, _lastAppActive); }
        }

        public void NotifyAppActive(bool active)
        {
            _lastAppActive = active;
            GlobusLogHelper.log.Debug(LogTag + " Visibility hint: tabVisible=" + _lastTabVisible + " appActive=" + _lastAppActive);
            if (_poller != null) { _poller.SetVisibilityHint(_lastTabVisible, _lastAppActive); }
        }

        // #W.UI — exposes the live poller (for the indicator dot to subscribe to IntervalChanged
        // and read LastPollUtc / LastPollStatus / CurrentIntervalSeconds for its tooltip).
        public FanvueNotificationPoller ActivePoller { get { return _poller; } }
        public event EventHandler PollerReplaced;
        private void RaisePollerReplaced()
        {
            try { var h = PollerReplaced; if (h != null) { h(this, EventArgs.Empty); } } catch { }
        }

        private void CancelInFlight()
        {
            try
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    _cts.Dispose();
                    _cts = null;
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
                    GlobusLogHelper.log.Warn(LogTag + " OnCredentialsUpdated: no account match for refreshed credentials");
                    return;
                }

                account.Credentials = e.Credentials;

                var accountVm = InstanceProvider.GetInstance<IDominatorAccountViewModel>();
                var dominatorAccount = accountVm != null
                    ? accountVm.LstDominatorAccountModel.FirstOrDefault(x =>
                        x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue
                        && x.AccountBaseModel.UserName == e.Credentials.Username)
                    : null;
                if (dominatorAccount != null)
                {
                    dominatorAccount.ModulePrivateDetails = JsonConvert.SerializeObject(e.Credentials);
                    dominatorAccount.IsUserLoggedIn = true;
                    // Persist to AccountDetails.bin — without this, refreshed tokens stay in RAM only
                    // and are lost on app restart (mirrors OutlookEmailScanService.cs:206 pattern).
                    try { InstanceProvider.GetInstance<IAccountsFileManager>()?.Edit(dominatorAccount); } catch { }
                }

                var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var socinatorPath = Path.Combine(appDataPath, "Socinator1.0");
                if (!Directory.Exists(socinatorPath)) { Directory.CreateDirectory(socinatorPath); }
                var credentialsPath = Path.Combine(socinatorPath, "FanvueCredentials.json");
                File.WriteAllText(credentialsPath, JsonConvert.SerializeObject(e.Credentials, Formatting.Indented));
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Warn(LogTag + " OnCredentialsUpdated failed: " + ex.Message);
            }
        }

        private void LoadAccounts()
        {
            try
            {
                _accounts.Clear();
                var accountVm = InstanceProvider.GetInstance<IDominatorAccountViewModel>();
                if (accountVm == null) { return; }

                var fanvue = accountVm.LstDominatorAccountModel
                    .Where(x => x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue)
                    .ToList();

                foreach (var acc in fanvue)
                {
                    var creds = TryReadCredentials(acc);
                    if (creds != null && creds.IsConnected)
                    {
                        _accounts.Add(new FanvueAccountOption
                        {
                            Username = creds.Username,
                            Email = creds.Email,
                            Credentials = creds
                        });
                    }
                }

                if (_accounts.Count > 0 && _selectedAccount == null)
                {
                    SelectedAccount = _accounts[0];
                }
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " LoadAccounts failed: " + ex.Message);
            }
        }

        private FanvueCredentials TryReadCredentials(DominatorAccountModel account)
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

        private async Task LoadConversationsAsync()
        {
            if (_selectedAccount == null) { return; }

            CancelInFlight();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                IsBusy = true;
                StatusMessage = "Loading conversations...";
                _conversations.Clear();
                _messages.Clear();
                _media.Clear();

                var client = new FanvueApiClient(_authService) { Credentials = _selectedAccount.Credentials };

                var unread = await client.GetUnreadCountsAsync(token);
                if (unread.IsSuccess && unread.Data != null)
                {
                    UnreadChats = unread.Data.UnreadChatsCount;
                    UnreadMessages = unread.Data.UnreadMessagesCount;
                }

                var activeFilters = GetActiveFilterValues();
                var search = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText.Trim();
                var chats = await client.GetChatsAsync(token, 50, null, activeFilters, search, null);
                if (!chats.IsSuccess || chats.Data == null)
                {
                    StatusMessage = chats.ErrorMessage ?? "Failed to load chats.";
                    return;
                }

                var items = chats.Data["data"] as JArray;
                if (items != null)
                {
                    foreach (var item in items)
                    {
                        var jobj = item as JObject;
                        if (jobj == null) { continue; }

                        var user = jobj["user"] as JObject;
                        var lastMsg = jobj["lastMessage"] as JObject;

                        var conv = new ChatConversationItem
                        {
                            UserUuid = user != null ? user["uuid"]?.ToString() : null,
                            Handle = user != null ? user["handle"]?.ToString() : null,
                            DisplayName = user != null ? user["displayName"]?.ToString() : null,
                            Nickname = user != null ? user["nickname"]?.ToString() : null,
                            AvatarUrl = user != null ? user["avatarUrl"]?.ToString() : null,
                            IsTopSpender = user != null ? (user["isTopSpender"]?.Value<bool>() ?? false) : false,
                            RegisteredAt = user != null ? user["registeredAt"]?.Value<DateTime?>() : null,
                            LastMessagePreview = lastMsg != null ? lastMsg["text"]?.ToString() : null,
                            LastMessageAt = lastMsg != null ? lastMsg["sentAt"]?.Value<DateTime?>() : null,
                            UnreadCount = jobj["unreadMessagesCount"]?.Value<int>() ?? 0,
                            IsRead = jobj["isRead"]?.Value<bool>() ?? false,
                            IsMuted = jobj["isMuted"]?.Value<bool>() ?? false
                        };
                        _conversations.Add(conv);
                    }
                }

                var uuids = _conversations
                    .Where(c => !string.IsNullOrEmpty(c.UserUuid))
                    .Select(c => c.UserUuid)
                    .Take(100)
                    .ToList();

                if (uuids.Count > 0)
                {
                    var statuses = await client.GetChatStatusesAsync(uuids, token);
                    if (statuses.IsSuccess && statuses.Data != null && statuses.Data.Statuses != null)
                    {
                        foreach (var s in statuses.Data.Statuses)
                        {
                            var match = _conversations.FirstOrDefault(c => c.UserUuid == s.UserUuid);
                            if (match != null)
                            {
                                match.IsOnline = s.IsOnline;
                                match.LastSeenAt = s.LastSeenAt;
                            }
                        }
                    }
                }

                StatusMessage = _conversations.Count + " conversations loaded.";
            }
            catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " LoadConversationsAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadThreadAsync(ChatConversationItem conv)
        {
            if (conv == null || _selectedAccount == null || string.IsNullOrEmpty(conv.UserUuid)) { return; }

            CancelInFlight();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                IsBusy = true;
                _messages.Clear();
                _media.Clear();

                var client = new FanvueApiClient(_authService) { Credentials = _selectedAccount.Credentials };

                const int pageSize = 20;
                const int maxPages = 10;
                var collected = new List<ChatThreadMessage>();
                var creatorUuid = _selectedAccount.Credentials != null ? _selectedAccount.Credentials.UserUuid : null;
                for (int page = 1; page <= maxPages; page++)
                {
                    if (token.IsCancellationRequested) { break; }

                    var thread = await client.GetChatMessagesAsync(conv.UserUuid, token, page, pageSize, page == 1);
                    if (!thread.IsSuccess || thread.Data == null || thread.Data.Data == null) { break; }

                    foreach (var m in thread.Data.Data)
                    {
                        // Prefer comparing SenderUuid to creator's own UUID. Fall back to
                        // "not equal to fan's UUID" when creator UUID is unknown (1:1 chat assumption).
                        bool isOutgoing;
                        if (!string.IsNullOrEmpty(creatorUuid) && !string.IsNullOrEmpty(m.SenderUuid))
                        {
                            isOutgoing = string.Equals(m.SenderUuid, creatorUuid, StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            isOutgoing = !string.IsNullOrEmpty(m.SenderUuid)
                                && !string.Equals(m.SenderUuid, conv.UserUuid, StringComparison.OrdinalIgnoreCase);
                        }
                        collected.Add(new ChatThreadMessage
                        {
                            Uuid = m.Uuid,
                            Text = m.Text,
                            SentAt = m.SentAt,
                            HasMedia = m.HasMedia,
                            MediaType = m.MediaType,
                            SenderUuid = m.SenderUuid,
                            Price = m.Price,
                            MediaUuids = m.MediaUuids,
                            IsOutgoing = isOutgoing
                        });
                    }

                    if (thread.Data.Pagination == null || !thread.Data.Pagination.HasMore) { break; }
                }

                // API returns newest-first; reverse so the UI lists oldest-first chronologically.
                collected.Reverse();
                foreach (var msg in collected) { _messages.Add(msg); }

                var media = await client.GetChatMediaAsync(conv.UserUuid, token, null, null, 20);
                if (media.IsSuccess && media.Data != null && media.Data.Data != null)
                {
                    foreach (var item in media.Data.Data)
                    {
                        _media.Add(new ChatThreadMedia
                        {
                            Uuid = item.Uuid,
                            MediaType = item.MediaType,
                            Url = item.Url,
                            ThumbnailUrl = item.ThumbnailUrl,
                            CreatedAt = item.CreatedAt
                        });
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " LoadThreadAsync failed: " + ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanSendMessage()
        {
            return _selectedAccount != null
                && _selectedConversation != null
                && !string.IsNullOrEmpty(_selectedConversation.UserUuid)
                && !string.IsNullOrWhiteSpace(_composerText)
                && !_isSending;
        }

        private async Task SendMessageAsync()
        {
            GlobusLogHelper.log.Debug(LogTag + " SendMessageCommand entered");

            var conv = _selectedConversation;
            var account = _selectedAccount;
            var text = _composerText;

            if (account == null || conv == null || string.IsNullOrEmpty(conv.UserUuid))
            {
                GlobusLogHelper.log.Warn(LogTag + " SendMessage aborted: account or conversation is null");
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                GlobusLogHelper.log.Warn(LogTag + " SendMessage aborted: text is empty");
                return;
            }

            try
            {
                IsSending = true;
                StatusMessage = "Sending...";

                var client = new FanvueApiClient(_authService) { Credentials = account.Credentials };
                var req = new ChatMessageSendRequest { Text = text.Trim() };

                GlobusLogHelper.log.Info(LogTag + " SendMessage start to=" + conv.UserUuid + " bodyLen=" + text.Length);

                // Send uses its own short-lived CTS so a concurrent conversation/filter
                // change cannot cancel an in-flight POST via the shared read-pipeline _cts.
                var sendCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                var result = await client.SendChatMessageAsync(conv.UserUuid, req, sendCts.Token);

                if (result.IsSuccess)
                {
                    string newUuid = result.Data != null ? result.Data.MessageUuid : null;
                    GlobusLogHelper.log.Info(LogTag + " SendMessage success messageUuid=" + (newUuid ?? "(null)"));

                    var creatorUuid = account.Credentials != null ? account.Credentials.UserUuid : null;
                    var sentMsg = new ChatThreadMessage
                    {
                        Uuid = newUuid,
                        Text = text.Trim(),
                        SentAt = DateTime.Now,
                        IsOutgoing = true,
                        SenderUuid = creatorUuid
                    };

                    if (Application.Current != null && Application.Current.Dispatcher != null
                        && !Application.Current.Dispatcher.CheckAccess())
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            _messages.Add(sentMsg);
                            ComposerText = string.Empty;
                            conv.LastMessagePreview = sentMsg.Text;
                            conv.LastMessageAt = sentMsg.SentAt;
                        }));
                    }
                    else
                    {
                        _messages.Add(sentMsg);
                        ComposerText = string.Empty;
                        conv.LastMessagePreview = sentMsg.Text;
                        conv.LastMessageAt = sentMsg.SentAt;
                    }

                    StatusMessage = "Sent.";
                }
                else
                {
                    GlobusLogHelper.log.Warn(LogTag + " SendMessage failed: " + (result.ErrorMessage ?? "(no error message)"));
                    StatusMessage = "Send failed: " + (result.ErrorMessage ?? "unknown error");
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Cancelled.";
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " SendMessage exception: " + ex.Message);
                StatusMessage = "Send error: " + ex.Message;
            }
            finally
            {
                IsSending = false;
            }
        }

        private async Task UpdateChatAsync(ChatConversationItem conv, bool? isRead, bool? isMuted)
        {
            if (conv == null || _selectedAccount == null || string.IsNullOrEmpty(conv.UserUuid)) { return; }

            try
            {
                var client = new FanvueApiClient(_authService) { Credentials = _selectedAccount.Credentials };
                var req = new ChatUpdateRequest { IsRead = isRead, IsMuted = isMuted };
                var token = _cts != null ? _cts.Token : CancellationToken.None;
                var result = await client.UpdateChatAsync(conv.UserUuid, req, token);
                if (result.IsSuccess)
                {
                    if (isRead == true) { conv.UnreadCount = 0; }
                }
                else
                {
                    StatusMessage = result.ErrorMessage;
                }
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " UpdateChatAsync failed: " + ex.Message);
            }
        }
    }
}
