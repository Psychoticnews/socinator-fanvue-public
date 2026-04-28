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
using FanvueDominatorUI.Views.Engage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Prism.Commands;
using Prism.Mvvm;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FanvueDominatorUI.ViewModels.Engage
{
    public class VaultFolderItem : BindableBase
    {
        public string Uuid { get; set; }
        public string Name { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int MediaCount { get; set; }
    }

    public class VaultMediaItem : BindableBase
    {
        public string Uuid { get; set; }
        public string MediaType { get; set; }
        public string Url { get; set; }
        public string ThumbnailUrl { get; set; }
        public DateTime? CreatedAt { get; set; }
    }

    public class VaultViewModel : BindableBase
    {
        private const string LogTag = "[FanvueVault]";

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
                    _ = LoadFoldersAsync();
                }
            }
        }

        public ObservableCollection<VaultFolderItem> Folders { get; } = new ObservableCollection<VaultFolderItem>();
        public ObservableCollection<VaultMediaItem> Media { get; } = new ObservableCollection<VaultMediaItem>();

        private VaultFolderItem _selectedFolder;
        public VaultFolderItem SelectedFolder
        {
            get { return _selectedFolder; }
            set
            {
                if (SetProperty(ref _selectedFolder, value))
                {
                    if (RenameFolderCommand != null) { RenameFolderCommand.RaiseCanExecuteChanged(); }
                    if (DeleteFolderCommand != null) { DeleteFolderCommand.RaiseCanExecuteChanged(); }
                    if (AddMediaCommand != null) { AddMediaCommand.RaiseCanExecuteChanged(); }
                    if (value != null) { _ = LoadFolderMediaAsync(value); }
                }
            }
        }

        private bool _isBusy;
        public bool IsBusy { get { return _isBusy; } set { SetProperty(ref _isBusy, value); } }

        private string _statusMessage;
        public string StatusMessage { get { return _statusMessage; } set { SetProperty(ref _statusMessage, value); } }

        public DelegateCommand RefreshCommand { get; }
        public DelegateCommand RefreshAccountsCommand { get; }
        public DelegateCommand AddFolderCommand { get; }
        public DelegateCommand RenameFolderCommand { get; }
        public DelegateCommand DeleteFolderCommand { get; }
        public DelegateCommand AddMediaCommand { get; }
        public DelegateCommand<VaultMediaItem> RemoveMediaCommand { get; }

        private CancellationTokenSource _writeCts;

        public VaultViewModel()
        {
            RefreshCommand = new DelegateCommand(async () => await LoadFoldersAsync());
            RefreshAccountsCommand = new DelegateCommand(LoadAccounts);
            AddFolderCommand = new DelegateCommand(async () => await AddFolderAsync());
            RenameFolderCommand = new DelegateCommand(async () => await RenameFolderAsync(), () => SelectedFolder != null);
            DeleteFolderCommand = new DelegateCommand(async () => await DeleteFolderAsync(), () => SelectedFolder != null);
            AddMediaCommand = new DelegateCommand(async () => await AddMediaAsync(), () => SelectedFolder != null);
            RemoveMediaCommand = new DelegateCommand<VaultMediaItem>(async media => await RemoveMediaAsync(media));

            _authService.CredentialsUpdated += OnCredentialsUpdated;

            LoadAccounts();
        }

        public void Cancel()
        {
            try
            {
                if (_cts != null) { _cts.Cancel(); _cts.Dispose(); _cts = null; }
                if (_writeCts != null) { _writeCts.Cancel(); _writeCts.Dispose(); _writeCts = null; }
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

        private async Task LoadFoldersAsync()
        {
            if (SelectedAccount == null) { return; }

            Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                IsBusy = true;
                StatusMessage = "Loading folders...";
                Folders.Clear();
                Media.Clear();

                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };
                var resp = await client.GetVaultFoldersAsync(token, 50, 1);
                if (!resp.IsSuccess || resp.Data == null)
                {
                    StatusMessage = resp.ErrorMessage ?? "Failed to load folders.";
                    return;
                }

                var data = resp.Data["data"] as JArray;
                if (data != null)
                {
                    foreach (var item in data)
                    {
                        var jobj = item as JObject;
                        if (jobj == null) { continue; }
                        Folders.Add(new VaultFolderItem
                        {
                            Uuid = jobj["uuid"]?.ToString(),
                            Name = jobj["name"]?.ToString(),
                            CreatedAt = jobj["createdAt"]?.Value<DateTime?>(),
                            MediaCount = jobj["mediaCount"]?.Value<int>() ?? 0
                        });
                    }
                }

                StatusMessage = Folders.Count + " folders.";
            }
            catch (OperationCanceledException) { StatusMessage = "Cancelled."; }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " LoadFoldersAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private async Task LoadFolderMediaAsync(VaultFolderItem folder)
        {
            if (folder == null || SelectedAccount == null || string.IsNullOrEmpty(folder.Uuid)) { return; }

            Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                IsBusy = true;
                StatusMessage = "Loading folder contents...";
                Media.Clear();

                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };
                var resp = await client.GetVaultFolderMediaAsync(folder.Uuid, token, 1, 50);
                if (!resp.IsSuccess || resp.Data == null)
                {
                    StatusMessage = resp.ErrorMessage ?? "Failed to load media.";
                    return;
                }

                if (resp.Data.Data != null)
                {
                    foreach (var m in resp.Data.Data)
                    {
                        Media.Add(new VaultMediaItem
                        {
                            Uuid = m.Uuid,
                            MediaType = m.MediaType,
                            Url = m.Url,
                            ThumbnailUrl = m.ThumbnailUrl,
                            CreatedAt = m.CreatedAt
                        });
                    }
                }

                StatusMessage = Media.Count + " media items.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " LoadFolderMediaAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private Window FindOwnerWindow()
        {
            try
            {
                var app = Application.Current;
                if (app == null) { return null; }
                var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
                if (active != null) { return active; }
                return app.MainWindow;
            }
            catch { return null; }
        }

        private CancellationToken BeginWrite()
        {
            if (_writeCts != null) { try { _writeCts.Cancel(); _writeCts.Dispose(); } catch { } }
            _writeCts = new CancellationTokenSource();
            return _writeCts.Token;
        }

        private async Task AddFolderAsync()
        {
            if (SelectedAccount == null) { return; }
            var dialog = new VaultFolderEditDialog("New Folder", string.Empty);
            var owner = FindOwnerWindow();
            if (owner != null) { dialog.Owner = owner; }
            if (dialog.ShowDialog() != true) { return; }
            if (string.IsNullOrWhiteSpace(dialog.Result)) { return; }

            var token = BeginWrite();
            try
            {
                IsBusy = true;
                StatusMessage = "Creating folder...";
                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };
                var resp = await client.CreateVaultFolderAsync(new VaultFolderCreateRequest { Name = dialog.Result }, token);
                if (!resp.IsSuccess)
                {
                    GlobusLogHelper.log.Error(LogTag + " CreateVaultFolderAsync failed: " + resp.ErrorMessage);
                    StatusMessage = resp.ErrorMessage ?? "Failed to create folder.";
                    return;
                }
                GlobusLogHelper.log.Info(LogTag + " Folder created: " + dialog.Result);
                StatusMessage = "Folder created.";
                await LoadFoldersAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " AddFolderAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private async Task RenameFolderAsync()
        {
            var folder = SelectedFolder;
            if (folder == null || SelectedAccount == null || string.IsNullOrEmpty(folder.Uuid)) { return; }
            var existingUuid = folder.Uuid;
            var dialog = new VaultFolderEditDialog("Rename Folder", folder.Name);
            var owner = FindOwnerWindow();
            if (owner != null) { dialog.Owner = owner; }
            if (dialog.ShowDialog() != true) { return; }
            if (string.IsNullOrWhiteSpace(dialog.Result)) { return; }

            var token = BeginWrite();
            try
            {
                IsBusy = true;
                StatusMessage = "Renaming folder...";
                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };
                var resp = await client.UpdateVaultFolderAsync(existingUuid, new VaultFolderUpdateRequest { Name = dialog.Result }, token);
                if (!resp.IsSuccess)
                {
                    GlobusLogHelper.log.Error(LogTag + " UpdateVaultFolderAsync failed: " + resp.ErrorMessage);
                    StatusMessage = resp.ErrorMessage ?? "Failed to rename folder.";
                    return;
                }
                GlobusLogHelper.log.Info(LogTag + " Folder renamed to: " + dialog.Result);
                StatusMessage = "Folder renamed.";
                await LoadFoldersAsync();
                var match = Folders.FirstOrDefault(f => f.Uuid == existingUuid);
                if (match != null) { SelectedFolder = match; }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " RenameFolderAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private async Task DeleteFolderAsync()
        {
            var folder = SelectedFolder;
            if (folder == null || SelectedAccount == null || string.IsNullOrEmpty(folder.Uuid)) { return; }

            var owner = FindOwnerWindow();
            var prompt = "Delete folder \"" + (folder.Name ?? string.Empty) + "\"? This cannot be undone.";
            MessageBoxResult confirm;
            if (owner != null)
            {
                confirm = MessageBox.Show(owner, prompt, "Delete Folder", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }
            else
            {
                confirm = MessageBox.Show(prompt, "Delete Folder", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }
            if (confirm != MessageBoxResult.Yes) { return; }

            var token = BeginWrite();
            try
            {
                IsBusy = true;
                StatusMessage = "Deleting folder...";
                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };
                var resp = await client.DeleteVaultFolderAsync(folder.Uuid, token);
                if (!resp.IsSuccess)
                {
                    GlobusLogHelper.log.Error(LogTag + " DeleteVaultFolderAsync failed: " + resp.ErrorMessage);
                    StatusMessage = resp.ErrorMessage ?? "Failed to delete folder.";
                    return;
                }
                GlobusLogHelper.log.Info(LogTag + " Folder deleted: " + folder.Name);
                StatusMessage = "Folder deleted.";
                await LoadFoldersAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " DeleteFolderAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private async Task AddMediaAsync()
        {
            var folder = SelectedFolder;
            if (folder == null || SelectedAccount == null || string.IsNullOrEmpty(folder.Uuid)) { return; }

            var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };
            var dialog = new VaultMediaPickerDialog(client);
            var owner = FindOwnerWindow();
            if (owner != null) { dialog.Owner = owner; }
            if (dialog.ShowDialog() != true) { return; }
            var uuids = dialog.SelectedUuids;
            if (uuids == null || uuids.Count == 0) { return; }

            var token = BeginWrite();
            try
            {
                IsBusy = true;
                StatusMessage = "Attaching " + uuids.Count + " items...";
                var attachClient = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };
                var resp = await attachClient.AttachVaultFolderMediaAsync(folder.Uuid, new VaultFolderAttachMediaRequest { MediaUuids = uuids }, token);
                if (!resp.IsSuccess)
                {
                    GlobusLogHelper.log.Error(LogTag + " AttachVaultFolderMediaAsync failed: " + resp.ErrorMessage);
                    StatusMessage = resp.ErrorMessage ?? "Failed to attach media.";
                    return;
                }
                GlobusLogHelper.log.Info(LogTag + " Attached " + uuids.Count + " items to folder: " + folder.Name);
                StatusMessage = "Attached " + uuids.Count + " items.";
                await LoadFolderMediaAsync(folder);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " AddMediaAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }

        private async Task RemoveMediaAsync(VaultMediaItem media)
        {
            if (media == null || string.IsNullOrEmpty(media.Uuid)) { return; }
            var folder = SelectedFolder;
            if (folder == null || SelectedAccount == null || string.IsNullOrEmpty(folder.Uuid)) { return; }

            var owner = FindOwnerWindow();
            var prompt = "Remove this item from \"" + (folder.Name ?? string.Empty) + "\"?";
            MessageBoxResult confirm;
            if (owner != null)
            {
                confirm = MessageBox.Show(owner, prompt, "Remove Media", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }
            else
            {
                confirm = MessageBox.Show(prompt, "Remove Media", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            }
            if (confirm != MessageBoxResult.Yes) { return; }

            var token = BeginWrite();
            try
            {
                IsBusy = true;
                StatusMessage = "Removing item...";
                var client = new FanvueApiClient(_authService) { Credentials = SelectedAccount.Credentials };
                var resp = await client.DetachVaultFolderMediaAsync(folder.Uuid, media.Uuid, token);
                if (!resp.IsSuccess)
                {
                    GlobusLogHelper.log.Error(LogTag + " DetachVaultFolderMediaAsync failed: " + resp.ErrorMessage);
                    StatusMessage = resp.ErrorMessage ?? "Failed to remove item.";
                    return;
                }
                GlobusLogHelper.log.Info(LogTag + " Detached 1 item from folder: " + folder.Name);
                StatusMessage = "Item removed.";
                await LoadFolderMediaAsync(folder);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " RemoveMediaAsync failed: " + ex.Message);
                StatusMessage = "Error: " + ex.Message;
            }
            finally { IsBusy = false; }
        }
    }
}
