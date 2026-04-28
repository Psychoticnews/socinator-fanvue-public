using DominatorHouseCore.LogHelper;
using FanvueDominatorCore.Services;
using FanvueDominatorUI.ViewModels.Engage;
using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FanvueDominatorUI.Views.Engage
{
    public partial class ChatMonitorControl : UserControl
    {
        private const string LogTag = "[FanvueChat]";

        // #W.UI — captures the window we hooked Activated/Deactivated on so we can detach cleanly.
        private Window _hostWindow;
        private FanvueNotificationPoller _hookedPoller;
        private DispatcherTimer _liveDotTooltipTimer;

        public ChatMonitorControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ChatMonitorViewModel;
            if (vm == null) { return; }
            vm.Messages.CollectionChanged += OnMessagesCollectionChanged;

            // #W.UI — visibility hint wiring.
            _hostWindow = Window.GetWindow(this);
            bool appActive = _hostWindow != null && _hostWindow.IsActive;
            vm.NotifyTabVisible(IsVisible);
            vm.NotifyAppActive(appActive);
            if (_hostWindow != null)
            {
                _hostWindow.Activated += OnHostWindowActivated;
                _hostWindow.Deactivated += OnHostWindowDeactivated;
            }

            // #W.UI — live dot wiring.
            vm.PollerReplaced += OnPollerReplaced;
            HookActivePoller(vm);
            UpdateLiveDot();

            // Refresh the dot tooltip every second so "Last: h:mm:ss" stays current.
            _liveDotTooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveDotTooltipTimer.Tick += (s, ev) => UpdateLiveDotTooltip();
            _liveDotTooltipTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ChatMonitorViewModel;
            if (vm != null)
            {
                vm.Messages.CollectionChanged -= OnMessagesCollectionChanged;
                vm.NotifyTabVisible(false);
                vm.PollerReplaced -= OnPollerReplaced;
                vm.Cancel();
            }
            UnhookActivePoller();
            if (_hostWindow != null)
            {
                _hostWindow.Activated -= OnHostWindowActivated;
                _hostWindow.Deactivated -= OnHostWindowDeactivated;
                _hostWindow = null;
            }
            if (_liveDotTooltipTimer != null)
            {
                _liveDotTooltipTimer.Stop();
                _liveDotTooltipTimer = null;
            }
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var vm = DataContext as ChatMonitorViewModel;
            if (vm == null) { return; }
            vm.NotifyTabVisible(IsVisible);
        }

        private void OnHostWindowActivated(object sender, EventArgs e)
        {
            var vm = DataContext as ChatMonitorViewModel;
            if (vm != null) { vm.NotifyAppActive(true); }
        }

        private void OnHostWindowDeactivated(object sender, EventArgs e)
        {
            var vm = DataContext as ChatMonitorViewModel;
            if (vm != null) { vm.NotifyAppActive(false); }
        }

        private void OnPollerReplaced(object sender, EventArgs e)
        {
            var vm = sender as ChatMonitorViewModel;
            if (vm == null) { vm = DataContext as ChatMonitorViewModel; }
            UnhookActivePoller();
            HookActivePoller(vm);
            UpdateLiveDot();
        }

        private void HookActivePoller(ChatMonitorViewModel vm)
        {
            if (vm == null || vm.ActivePoller == null) { return; }
            _hookedPoller = vm.ActivePoller;
            _hookedPoller.IntervalChanged += OnPollerIntervalChanged;
        }

        private void UnhookActivePoller()
        {
            if (_hookedPoller != null)
            {
                try { _hookedPoller.IntervalChanged -= OnPollerIntervalChanged; } catch { }
                _hookedPoller = null;
            }
        }

        private void OnPollerIntervalChanged(object sender, int newIntervalSeconds)
        {
            try
            {
                Dispatcher.BeginInvoke(new Action(UpdateLiveDot));
            }
            catch { }
        }

        // #W.UI — updates the live dot brush + tooltip based on current poller state. Called on
        // initial load, on poller IntervalChanged, and whenever the poller is replaced.
        private void UpdateLiveDot()
        {
            try
            {
                if (LivePollDot == null) { return; }
                var vm = DataContext as ChatMonitorViewModel;
                var poller = (vm != null) ? vm.ActivePoller : null;
                Color color;
                if (poller == null)
                {
                    color = (Color)ColorConverter.ConvertFromString("#555555");
                }
                else
                {
                    int interval = poller.CurrentIntervalSeconds;
                    if (interval <= 15)
                    {
                        // 10s default foreground = green
                        color = (Color)ColorConverter.ConvertFromString("#4CAF50");
                    }
                    else if (interval >= 30 && interval < 60)
                    {
                        // double-hold while foreground (10s -> 20s) = orange
                        color = (Color)ColorConverter.ConvertFromString("#FF9800");
                    }
                    else if (interval >= 100)
                    {
                        // double-hold while background (60s -> 120s) = orange
                        color = (Color)ColorConverter.ConvertFromString("#FF9800");
                    }
                    else
                    {
                        // 60s background = gray
                        color = (Color)ColorConverter.ConvertFromString("#888888");
                    }
                }
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                LivePollDot.Fill = brush;
                UpdateLiveDotTooltip();
            }
            catch (Exception ex)
            {
                GlobusLogHelper.log.Warn(LogTag + " UpdateLiveDot failed: " + ex.Message);
            }
        }

        private void UpdateLiveDotTooltip()
        {
            try
            {
                if (LivePollDot == null) { return; }
                var vm = DataContext as ChatMonitorViewModel;
                var poller = (vm != null) ? vm.ActivePoller : null;
                if (poller == null)
                {
                    LivePollDot.ToolTip = "Polling: not started (no account selected)";
                    return;
                }
                string lastTime = poller.LastPollUtc == DateTime.MinValue
                    ? "(never)"
                    : poller.LastPollUtc.ToLocalTime().ToString("h:mm:ss tt");
                string status = string.IsNullOrEmpty(poller.LastPollStatus) ? "(no status yet)" : poller.LastPollStatus;
                LivePollDot.ToolTip = "Polling every " + poller.CurrentIntervalSeconds + "s · Last: " + lastTime + " · Status: " + status;
            }
            catch { }
        }

        private void OnMessagesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            try
            {
                if (ThreadScrollViewer != null)
                {
                    ThreadScrollViewer.ScrollToBottom();
                }
            }
            catch { }
        }

        private void ComposerTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key != Key.Enter && e.Key != Key.Return) { return; }

                bool shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                if (shiftHeld)
                {
                    return;
                }

                GlobusLogHelper.log.Debug(LogTag + " ComposerTextBox_KeyDown Enter pressed -> invoking SendMessageCommand");

                var vm = DataContext as ChatMonitorViewModel;
                if (vm == null) { return; }

                if (vm.SendMessageCommand != null && vm.SendMessageCommand.CanExecute())
                {
                    vm.SendMessageCommand.Execute();
                }
                e.Handled = true;
            }
            catch (System.Exception ex)
            {
                GlobusLogHelper.log.Warn(LogTag + " ComposerTextBox_KeyDown failed: " + ex.Message);
            }
        }
    }
}
