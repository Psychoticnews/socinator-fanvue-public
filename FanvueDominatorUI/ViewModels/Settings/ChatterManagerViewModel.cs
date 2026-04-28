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
using FanvueDominatorUI.ViewModels.Engage;
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

namespace FanvueDominatorUI.ViewModels.Settings
{
    public class CreatorAccessRow : BindableBase
    {
        public string CreatorUuid { get; set; }
        public string Role { get; set; }
        public bool IsAdmin { get { return string.Equals(Role, "ADMIN", StringComparison.OrdinalIgnoreCase); } }
    }

    public class TeamMemberRow : BindableBase
    {
        public string Uuid { get; set; }
        public string DisplayName { get; set; }
        public string Nickname { get; set; }
        public string Email { get; set; }
        public bool IsAdmin { get; set; }
        public List<CreatorAccessRow> CreatorAccess { get; set; } = new List<CreatorAccessRow>();
        public int CreatorCount { get { return CreatorAccess != null ? CreatorAccess.Count : 0; } }
    }

    public class CreatorRow : BindableBase
    {
        public string Uuid { get; set; }
        public string Handle { get; set; }
        public string DisplayName { get; set; }
        public string AvatarUrl { get; set; }
        public DateTime? RegisteredAt { get; set; }
    }

    public class ChatterManagerViewModel : BindableBase
    {
        private const string LogTag = "[FanvueChatterManager]";

        private readonly FanvueAuthService _authService = new FanvueAuthService();
        private CancellationTokenSource _cts;

        public ObservableCollection<FanvueAccountOption> Accounts { get; } = new ObservableCollection<FanvueAccountOption>();

        private FanvueAccountOption _selectedAccount;
        public FanvueAccountOption SelectedAccount
        {
            get { return _selectedAccount; }
            set
            {
                if (SetProperty(ref _selectedAccount, value) && value != null)
                {
                    _ = RefreshAsync();
                }
            }
        }

        public ObservableCollection<TeamMemberRow> TeamMembers { get; } = new ObservableCollection<TeamMemberRow>();
        public ObservableCollection<CreatorRow> Creators { get; } = new ObservableCollection<CreatorRow>();

        private TeamMemberRow _selectedMember;
        public TeamMemberRow SelectedMember { get { return _selectedMember; } set { SetProperty(ref _selectedMember, value); } }

        private bool _isBusy;
        public bool IsBusy { get { return _isBusy; } set { SetProperty(ref _isBusy, value); } }

        private string _statusMessage;
        public string StatusMessage { get { return _statusMessage; } set { SetProperty(ref _statusMessage, value); } }

        public DelegateCommand RefreshCommand { get; }
        public DelegateCommand RefreshAccountsCommand { get; }

        public ChatterManagerViewModel()
        {
            RefreshCommand = new DelegateCommand(async () => await RefreshAsync());
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

        private async Task RefreshAsync()
        {
            if (SelectedAccount == null) { return; }

            Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                IsBusy = true;
                StatusMessage = "Loading agency roster...";
                TeamMembers.Clear();
                Creators.Clear();

                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };

                var members = await client.GetAgencyTeamMembersAsync(token, 1, 50);
                if (members.IsSuccess && members.Data != null && members.Data.Data != null)
                {
                    foreach (var m in members.Data.Data)
                    {
                        var row = new TeamMemberRow
                        {
                            Uuid = m.Uuid,
                            DisplayName = m.DisplayName,
                            Nickname = m.Nickname,
                            Email = m.Email,
                            IsAdmin = m.IsAdmin
                        };
                        if (m.CreatorAccess != null)
                        {
                            foreach (var ca in m.CreatorAccess)
                            {
                                row.CreatorAccess.Add(new CreatorAccessRow { CreatorUuid = ca.Uuid, Role = ca.Role });
                            }
                        }
                        TeamMembers.Add(row);
                    }
                }

                var creators = await client.GetAgencyCreatorsAsync(token, 1, 50);
                if (creators.IsSuccess && creators.Data != null && creators.Data.Data != null)
                {
                    foreach (var c in creators.Data.Data)
                    {
                        Creators.Add(new CreatorRow
                        {
                            Uuid = c.Uuid,
                            Handle = c.Handle,
                            DisplayName = c.DisplayName,
                            AvatarUrl = c.AvatarUrl,
                            RegisteredAt = c.RegisteredAt
                        });
                    }
                }

                StatusMessage = TeamMembers.Count + " team members, " + Creators.Count + " creators.";
            }
            catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " RefreshAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }
    }
}
