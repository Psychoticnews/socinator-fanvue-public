using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace FanvueDominatorUI.Views.Notifications
{
    /// <summary>
    /// Lightweight non-blocking toast. 5s auto-dismiss. Stacks vertically (each new
    /// toast 85px below the prior open toast) on the primary monitor's WorkArea.
    /// Multi-monitor support is deferred.
    /// </summary>
    public partial class NotificationToast : Window
    {
        private const double ToastWidth = 360;
        private const double ToastHeight = 80;
        private const double VerticalSpacing = 85;
        private const double EdgeMargin = 12;

        private static readonly List<NotificationToast> _openToasts = new List<NotificationToast>();

        private DispatcherTimer _autoCloseTimer;

        public NotificationToast()
        {
            InitializeComponent();
        }

        public static void Show(string title, string body)
        {
            Show(title, body, null);
        }

        // #W.UI — overload for clickable toasts (used by NewMessage so user can navigate to the
        // conversation). onClick is invoked once on left-click; the toast closes immediately after.
        public static void Show(string title, string body, Action onClick)
        {
            try
            {
                if (Application.Current == null) { return; }
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var toast = new NotificationToast();
                    toast.TitleText.Text = title ?? string.Empty;
                    toast.BodyText.Text = body ?? string.Empty;
                    toast.PositionInStack();
                    _openToasts.Add(toast);
                    toast.Closed += (s, e) => _openToasts.Remove(toast);
                    if (onClick != null)
                    {
                        toast.Cursor = System.Windows.Input.Cursors.Hand;
                        toast.MouseLeftButtonUp += (s, e) =>
                        {
                            try { onClick(); } catch { }
                            try { toast.Close(); } catch { }
                        };
                    }
                    toast.StartAutoClose();
                    toast.Show();
                }));
            }
            catch
            {
                // Toast must never crash caller.
            }
        }

        private void PositionInStack()
        {
            var work = SystemParameters.WorkArea;
            double left = work.Right - ToastWidth - EdgeMargin;
            double top = work.Top + EdgeMargin + (_openToasts.Count * VerticalSpacing);
            // Clamp so we never run off the bottom edge — wrap to first slot.
            if (top + ToastHeight > work.Bottom)
            {
                top = work.Top + EdgeMargin;
            }
            Left = left;
            Top = top;
            Width = ToastWidth;
            Height = ToastHeight;
        }

        private void StartAutoClose()
        {
            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _autoCloseTimer.Tick += (s, e) =>
            {
                try
                {
                    _autoCloseTimer.Stop();
                    Close();
                }
                catch { }
            };
            _autoCloseTimer.Start();
        }
    }
}
