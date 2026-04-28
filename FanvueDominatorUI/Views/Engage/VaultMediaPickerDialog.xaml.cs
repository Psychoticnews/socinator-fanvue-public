using DominatorHouseCore.LogHelper;
using FanvueDominatorCore.Services;
using Newtonsoft.Json.Linq;
using Prism.Mvvm;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows;

namespace FanvueDominatorUI.Views.Engage
{
    public class PickableMediaItem : BindableBase
    {
        private bool _isSelected;
        public string Uuid { get; set; }
        public string MediaType { get; set; }
        public string ThumbnailUrl { get; set; }
        public bool IsSelected
        {
            get { return _isSelected; }
            set { SetProperty(ref _isSelected, value); }
        }
    }

    public partial class VaultMediaPickerDialog : Window
    {
        private const string LogTag = "[FanvueVault]";

        private readonly FanvueApiClient _client;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly ObservableCollection<PickableMediaItem> _items = new ObservableCollection<PickableMediaItem>();

        public List<string> SelectedUuids { get; private set; }

        public VaultMediaPickerDialog(FanvueApiClient client)
        {
            InitializeComponent();
            _client = client;
            SelectedUuids = new List<string>();
            MediaItems.ItemsSource = _items;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_client == null)
            {
                HeaderText.Text = "No account.";
                return;
            }

            try
            {
                HeaderText.Text = "Loading media...";
                var resp = await _client.GetMediaAsync(_cts.Token, null, 100, 1);
                if (!resp.IsSuccess || resp.Data == null)
                {
                    HeaderText.Text = "Failed to load media.";
                    StatusText.Text = resp.ErrorMessage ?? string.Empty;
                    return;
                }

                var data = resp.Data["data"] as JArray;
                int totalLoaded = 0;
                if (data != null)
                {
                    foreach (var item in data)
                    {
                        var jobj = item as JObject;
                        if (jobj == null) { continue; }
                        var uuid = jobj["uuid"]?.ToString();
                        if (string.IsNullOrEmpty(uuid)) { continue; }
                        _items.Add(new PickableMediaItem
                        {
                            Uuid = uuid,
                            MediaType = jobj["mediaType"]?.ToString(),
                            ThumbnailUrl = jobj["thumbnailUrl"]?.ToString() ?? jobj["url"]?.ToString()
                        });
                        totalLoaded++;
                    }
                }

                int? total = null;
                var meta = resp.Data["meta"] as JObject;
                if (meta != null && meta["total"] != null)
                {
                    total = meta["total"].Value<int?>();
                }

                if (total.HasValue && total.Value > totalLoaded)
                {
                    HeaderText.Text = "Showing " + totalLoaded + " of " + total.Value + " items";
                }
                else
                {
                    HeaderText.Text = "Showing " + totalLoaded + " items";
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Error(LogTag + " VaultMediaPickerDialog load failed: " + ex.Message);
                HeaderText.Text = "Error loading media.";
                StatusText.Text = ex.Message;
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            try { _cts.Cancel(); _cts.Dispose(); } catch { }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedUuids = _items.Where(i => i.IsSelected && !string.IsNullOrEmpty(i.Uuid))
                                  .Select(i => i.Uuid)
                                  .ToList();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
