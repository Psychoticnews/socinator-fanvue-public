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
using Newtonsoft.Json;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FanvueDominatorUI.ViewModels.Engage
{
    public class MassMessageRow : BindableBase
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Status { get; set; }
        public DateTime? ScheduledAt { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int RecipientCount { get; set; }
        public long? Price { get; set; }
        public bool IsScheduled { get { return string.Equals(Status, "scheduled", StringComparison.OrdinalIgnoreCase); } }
    }

    public class SmartListItem : BindableBase
    {
        private bool _isSelected;
        public string Uuid { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
        public bool IsSelected { get { return _isSelected; } set { SetProperty(ref _isSelected, value); } }
    }

    public class CustomListItem : BindableBase
    {
        private bool _isSelected;
        public string Uuid { get; set; }
        public string Name { get; set; }
        public int? MemberCount { get; set; }
        public bool IsSelected { get { return _isSelected; } set { SetProperty(ref _isSelected, value); } }
    }

    public class TemplateItem
    {
        public string Uuid { get; set; }
        public string Text { get; set; }
        public string FolderName { get; set; }
        public long? Price { get; set; }
        public List<string> MediaUuids { get; set; }
    }

    public class MassMessagesViewModel : BindableBase
    {
        private const string LogTag = "[FanvueMassMessages]";

        private readonly FanvueAuthService _authService = new FanvueAuthService();
        private CancellationTokenSource _cts;

        public ObservableCollection<FanvueAccountOption> Accounts { get; } = new ObservableCollection<FanvueAccountOption>();

        private FanvueAccountOption _selectedAccount;
        public FanvueAccountOption SelectedAccount
        {
            get { return _selectedAccount; }
            set
            {
                if (SetProperty(ref _selectedAccount, value))
                {
                    if (value != null) { _ = RefreshAllAsync(); }
                    SendCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public ObservableCollection<MassMessageRow> MassMessages { get; } = new ObservableCollection<MassMessageRow>();
        public ObservableCollection<SmartListItem> SmartLists { get; } = new ObservableCollection<SmartListItem>();
        public ObservableCollection<CustomListItem> CustomLists { get; } = new ObservableCollection<CustomListItem>();
        public ObservableCollection<TemplateItem> Templates { get; } = new ObservableCollection<TemplateItem>();

        private TemplateItem _selectedTemplate;
        public TemplateItem SelectedTemplate
        {
            get { return _selectedTemplate; }
            set
            {
                if (SetProperty(ref _selectedTemplate, value) && value != null)
                {
                    MessageText = value.Text;
                    Price = value.Price;
                }
            }
        }

        private string _messageText;
        public string MessageText
        {
            get { return _messageText; }
            set
            {
                if (SetProperty(ref _messageText, value))
                {
                    SendCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private long? _price;
        public long? Price { get { return _price; } set { SetProperty(ref _price, value); } }

        private DateTime? _scheduledAt;
        public DateTime? ScheduledAt { get { return _scheduledAt; } set { SetProperty(ref _scheduledAt, value); } }

        private string _mediaUuidsCsv;
        public string MediaUuidsCsv { get { return _mediaUuidsCsv; } set { SetProperty(ref _mediaUuidsCsv, value); } }

        private MassMessageRow _selectedRow;
        public MassMessageRow SelectedRow
        {
            get { return _selectedRow; }
            set
            {
                if (SetProperty(ref _selectedRow, value))
                {
                    DeleteCommand.RaiseCanExecuteChanged();
                    UpdateCommand.RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isBusy;
        public bool IsBusy { get { return _isBusy; } set { SetProperty(ref _isBusy, value); } }

        private string _statusMessage;
        public string StatusMessage { get { return _statusMessage; } set { SetProperty(ref _statusMessage, value); } }

        public DelegateCommand RefreshCommand { get; }
        public DelegateCommand SendCommand { get; }
        public DelegateCommand UpdateCommand { get; }
        public DelegateCommand DeleteCommand { get; }
        public DelegateCommand RefreshAccountsCommand { get; }

        public MassMessagesViewModel()
        {
            RefreshCommand = new DelegateCommand(async () => await RefreshAllAsync());
            SendCommand = new DelegateCommand(async () => await SendAsync(), CanSend);
            UpdateCommand = new DelegateCommand(async () => await UpdateAsync(), () => SelectedRow != null && SelectedRow.IsScheduled);
            DeleteCommand = new DelegateCommand(async () => await DeleteAsync(), () => SelectedRow != null);
            RefreshAccountsCommand = new DelegateCommand(LoadAccounts);

            _authService.CredentialsUpdated += OnCredentialsUpdated;

            LoadAccounts();
        }

        public void Cancel()
        {
            try
            {
                if (_cts != null) { _cts.Cancel(); _cts.Dispose(); _cts = null; }
                _authService.CredentialsUpdated -= OnCredentialsUpdated;
            }
            catch { }
        }

        private void OnCredentialsUpdated(object sender, CredentialsUpdatedEventArgs e)
        {
            try
            {
                if (e == null || e.Credentials == null) { return; }

                var account = Accounts?.FirstOrDefault(a => a.Username == e.Credentials.Username);
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

        private bool CanSend()
        {
            return SelectedAccount != null && !string.IsNullOrEmpty(MessageText);
        }

        private void LoadAccounts()
        {
            try
            {
                Accounts.Clear();
                var vm = InstanceProvider.GetInstance<IDominatorAccountViewModel>();
                if (vm == null) { return; }

                var fanvue = vm.LstDominatorAccountModel
                    .Where(x => x.AccountBaseModel.AccountNetwork == SocialNetworks.Fanvue)
                    .ToList();

                foreach (var acc in fanvue)
                {
                    var creds = TryReadCredentials(acc);
                    if (creds != null && creds.IsConnected)
                    {
                        Accounts.Add(new FanvueAccountOption
                        {
                            Username = creds.Username,
                            Email = creds.Email,
                            Credentials = creds
                        });
                    }
                }

                if (Accounts.Count > 0 && SelectedAccount == null) { SelectedAccount = Accounts[0]; }
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

        private async Task RefreshAllAsync()
        {
            if (SelectedAccount == null) { return; }

            Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                IsBusy = true;
                StatusMessage = "Loading mass messages...";
                MassMessages.Clear();
                Templates.Clear();
                SmartLists.Clear();
                CustomLists.Clear();

                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };

                var msgs = await client.GetMassMessagesAsync(token, 1, 50);
                if (msgs.IsSuccess && msgs.Data != null && msgs.Data.Data != null)
                {
                    foreach (var d in msgs.Data.Data)
                    {
                        MassMessages.Add(new MassMessageRow
                        {
                            Id = d.Id,
                            Text = d.Text,
                            Status = d.Status,
                            ScheduledAt = d.ScheduledAt,
                            CreatedAt = d.CreatedAt,
                            RecipientCount = d.RecipientCount,
                            Price = d.Price
                        });
                    }
                }

                var templates = await client.GetChatTemplatesAsync(token, 1, 50);
                if (templates.IsSuccess && templates.Data != null && templates.Data.Data != null)
                {
                    foreach (var t in templates.Data.Data)
                    {
                        Templates.Add(new TemplateItem
                        {
                            Uuid = t.Uuid,
                            Text = t.Text,
                            FolderName = t.FolderName,
                            Price = t.Price,
                            MediaUuids = t.MediaUuids
                        });
                    }
                }

                var smart = await client.GetSmartListsAsync(token);
                if (smart.IsSuccess && smart.Data != null && smart.Data.Data != null)
                {
                    foreach (var s in smart.Data.Data)
                    {
                        SmartLists.Add(new SmartListItem { Uuid = s.Uuid, Name = s.Name, Count = s.Count });
                    }
                }

                var custom = await client.GetCustomListsAsync(token, 1, 50);
                if (custom.IsSuccess && custom.Data != null && custom.Data.Data != null)
                {
                    foreach (var c in custom.Data.Data)
                    {
                        CustomLists.Add(new CustomListItem { Uuid = c.Uuid, Name = c.Name, MemberCount = c.MemberCount });
                    }
                }

                StatusMessage = MassMessages.Count + " mass messages, " + Templates.Count + " templates.";
            }
            catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " RefreshAllAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private MassMessageListsDto BuildLists()
        {
            var smartNames = SmartLists.Where(s => s.IsSelected).Select(s => s.Name).ToList();
            var customUuids = CustomLists.Where(c => c.IsSelected).Select(c => c.Uuid).ToList();
            if (smartNames.Count == 0 && customUuids.Count == 0) { return null; }
            return new MassMessageListsDto
            {
                SmartLists = smartNames.Count > 0 ? smartNames : null,
                CustomListUuids = customUuids.Count > 0 ? customUuids : null
            };
        }

        private List<string> ParseMediaUuids()
        {
            if (string.IsNullOrEmpty(MediaUuidsCsv)) { return null; }
            return MediaUuidsCsv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
        }

        private async Task SendAsync()
        {
            if (SelectedAccount == null || string.IsNullOrEmpty(MessageText)) { return; }

            Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                IsBusy = true;
                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };

                var includedLists = BuildLists();
                if (includedLists == null)
                {
                    StatusMessage = "Select at least one smart list or custom list as the audience.";
                    return;
                }

                var req = new MassMessageCreateRequest
                {
                    Text = MessageText,
                    Price = Price,
                    MediaUuids = ParseMediaUuids(),
                    ScheduledAt = ScheduledAt,
                    IncludedLists = includedLists
                };

                var result = await client.CreateMassMessageAsync(req, token);
                if (result.IsSuccess && result.Data != null)
                {
                    StatusMessage = ScheduledAt.HasValue ? "Scheduled." : "Sent.";
                    MessageText = string.Empty;
                    ScheduledAt = null;
                    await RefreshAllAsync();
                }
                else
                {
                    StatusMessage = result.ErrorMessage ?? "Send failed.";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " SendAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private async Task UpdateAsync()
        {
            if (SelectedAccount == null || SelectedRow == null || !SelectedRow.IsScheduled) { return; }

            try
            {
                IsBusy = true;
                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };

                var req = new MassMessageUpdateRequest
                {
                    Text = MessageText,
                    Price = Price,
                    MediaUuids = ParseMediaUuids(),
                    ScheduledAt = ScheduledAt
                };

                var token = _cts != null ? _cts.Token : CancellationToken.None;
                var result = await client.UpdateMassMessageAsync(SelectedRow.Id, req, token);
                StatusMessage = result.IsSuccess ? "Updated." : (result.ErrorMessage ?? "Update failed.");
                if (result.IsSuccess) { await RefreshAllAsync(); }
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " UpdateAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private async Task DeleteAsync()
        {
            if (SelectedAccount == null || SelectedRow == null) { return; }

            try
            {
                IsBusy = true;
                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };
                var token = _cts != null ? _cts.Token : CancellationToken.None;
                var result = await client.DeleteMassMessageAsync(SelectedRow.Id, token);
                StatusMessage = result.IsSuccess ? "Deleted." : (result.ErrorMessage ?? "Delete failed.");
                if (result.IsSuccess) { await RefreshAllAsync(); }
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " DeleteAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }
    }
}
