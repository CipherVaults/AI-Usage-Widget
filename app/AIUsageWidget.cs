using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Forms = System.Windows.Forms;
using System.Windows.Threading;

[assembly: AssemblyTitle("AI Usage Widget")]
[assembly: AssemblyDescription("Desktop quota monitor for Codex, Google Antigravity, and Claude")]
[assembly: AssemblyProduct("AI Usage Widget")]
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AIUsageWidget
{
    internal static class Widget
    {
        private const string SingleInstanceMutexName = "AIUsageWidget.SingleInstance";
        private const string ActivateEventName = "AIUsageWidget.Activate";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool SetDllDirectory(string pathName);

#if !PORTABLE
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
#endif

        [STAThread]
        public static void Main()
        {
            // Remove the current working directory from the unmanaged DLL search order.
            SetDllDirectory("");
            bool createdNew;
            using (var singleInstance = new Mutex(true, SingleInstanceMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    SignalExistingInstance();
                    return;
                }
#if !PORTABLE
                SetCurrentProcessExplicitAppUserModelID("AIUsageWidget.Desktop");
#endif
                using (var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName))
                {
                    new Application().Run(new AIUsageWidgetWindow(activateEvent));
                }
            }
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using (var activateEvent = EventWaitHandle.OpenExisting(ActivateEventName))
                    activateEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class AIUsageWidgetWindow : Window
    {
        private const double DefaultWidth = 360;
        private const int ExitSizeMove = 0x0232;
        private static readonly Brush Muted = BrushFrom("#A6ABB5");
        private static readonly Brush Amber = BrushFrom("#D8AD78");
        private static readonly Brush Red = BrushFrom("#DE8E90");
        private static readonly FontFamily NumericFont = new FontFamily("Consolas");
        private readonly DispatcherTimer clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly List<ResetLabel> resets = new List<ResetLabel>();
        private readonly ToggleButton pin;
        private readonly TextBlock status;
        private readonly ProviderCard codex;
        private readonly ProviderCard antigravity;
        private readonly ProviderCard claude;
        private readonly QuotaUpdates codexUpdates;
        private readonly QuotaUpdates antigravityUpdates;
        private readonly QuotaUpdates claudeUpdates;
        private readonly MenuItem closeToTrayOption;
        private Forms.NotifyIcon tray;
        private System.Drawing.Icon trayIcon;
        private readonly RegisteredWaitHandle activateWait;
        private bool updatesStarted;

        public AIUsageWidgetWindow(EventWaitHandle activateEvent = null)
        {
            if (activateEvent != null)
            {
                activateWait = ThreadPool.RegisterWaitForSingleObject(
                    activateEvent,
                    (state, timedOut) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RestoreFromTray)),
                    null, Timeout.Infinite, false);
            }
            Title = "AI Usage Widget";
            var assembly = Assembly.GetExecutingAssembly();
            using (var resource = assembly.GetManifestResourceStream("AIUsageWidget.Icon"))
            {
                var icon = new IconBitmapDecoder(resource, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Icon = icon.Frames[icon.Frames.Count - 1];
            }
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                UseAeroCaptionButtons = false
            });
            Background = BrushFrom("#0B0C0E");
            FontFamily = new FontFamily("Segoe UI");
            UseLayoutRounding = true;

            Border layout;
            using (var resource = assembly.GetManifestResourceStream("AIUsageWidget.Layout"))
                layout = (Border)XamlReader.Load(resource);
            ((Image)layout.FindName("HeaderIcon")).Source = Icon;
            Content = layout;
            var workArea = SystemParameters.WorkArea;
            MinWidth = 300;
            MinHeight = 152;
            MaxWidth = Math.Max(MinWidth, workArea.Width);
            MaxHeight = Math.Max(MinHeight, workArea.Height);
            ((ScrollViewer)layout.FindName("QuotaScroll")).MaxHeight = Math.Max(84, MaxHeight - 68);
            var cards = (Panel)layout.FindName("Cards");
            var quotaBarStyle = (Style)layout.Resources["QuotaBar"];
            var headerStyle = (Style)layout.Resources["ProviderHeader"];
            var settings = WidgetSettings.Load();
            Width = LimitSize(settings.Width ?? DefaultWidth, MinWidth, MaxWidth, DefaultWidth);
            SizeToContent = SizeToContent.Height;
            if (settings.Height > 0 && !double.IsInfinity(settings.Height.Value))
            {
                Height = LimitSize(settings.Height.Value, MinHeight, MaxHeight, MinHeight);
                SizeToContent = SizeToContent.Manual;
            }
            codex = new ProviderCard("Codex", "#B1A5FF", "#181523", quotaBarStyle, headerStyle, settings.CodexCollapsed, SaveWindow);
            antigravity = new ProviderCard("Antigravity", "#8AB6FF", "#101824", quotaBarStyle, headerStyle,
                settings.AntigravityCollapsed, SaveWindow, (Brush)layout.Resources["AntigravityQuotaBrush"]);
            claude = new ProviderCard("Claude", "#EAA58D", "#211715", quotaBarStyle, headerStyle,
                settings.ClaudeCollapsed, SaveWindow);
            antigravity.Container.BorderThickness = new Thickness(0, 1, 0, 0);
            claude.Container.BorderThickness = new Thickness(0, 1, 0, 0);
            cards.Children.Add(codex.Container);
            cards.Children.Add(antigravity.Container);
            cards.Children.Add(claude.Container);

            status = (TextBlock)layout.FindName("Status");
            var refresh = (Button)layout.FindName("RefreshButton");
            codexUpdates = new QuotaUpdates(QuotaSources.Codex, reading => ShowReading(codex, reading));
            antigravityUpdates = new QuotaUpdates(QuotaSources.Antigravity, reading => ShowReading(antigravity, reading));
            claudeUpdates = new QuotaUpdates(QuotaSources.Claude, reading => ShowReading(claude, reading));
            pin = (ToggleButton)layout.FindName("PinButton");
            Topmost = settings.Pinned;
            pin.IsChecked = Topmost;
            UpdatePinTooltip();
            pin.Click += (sender, args) =>
            {
                Topmost = pin.IsChecked == true;
                UpdatePinTooltip();
                SaveWindow();
            };
            var settingsButton = (Button)layout.FindName("SettingsButton");
            closeToTrayOption = new MenuItem
            {
                Header = "Close to tray",
                IsCheckable = true,
                IsChecked = settings.CloseToTray,
                ToolTip = "Keep the widget running when you click X"
            };
            closeToTrayOption.Click += (sender, args) => SaveWindow();
            var settingsMenu = new ContextMenu
            {
                PlacementTarget = settingsButton,
                Placement = PlacementMode.Bottom,
                Style = (Style)layout.Resources["WidgetMenu"]
            };
            settingsMenu.Items.Add(closeToTrayOption);
            var fitHeightOption = new MenuItem { Header = "Fit height to content" };
            fitHeightOption.Click += (sender, args) =>
            {
                Height = double.NaN;
                SizeToContent = SizeToContent.Height;
                SaveWindow();
            };
            settingsMenu.Items.Add(fitHeightOption);
            settingsButton.ContextMenu = settingsMenu;
            settingsButton.Click += (sender, args) => settingsMenu.IsOpen = true;

            Left = workArea.Right - Width - 16;
            Top = workArea.Top + 16;
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (settings.Left.HasValue && settings.Top.HasValue &&
                !double.IsNaN(settings.Left.Value) && !double.IsInfinity(settings.Left.Value) &&
                !double.IsNaN(settings.Top.Value) && !double.IsInfinity(settings.Top.Value) &&
                desktop.Contains(new Rect(settings.Left.Value, settings.Top.Value, Width, 48)))
            {
                Left = settings.Left.Value;
                Top = settings.Top.Value;
            }
            ((UIElement)layout.FindName("DragArea")).MouseLeftButtonDown += (sender, args) =>
            {
                if (args.LeftButton == MouseButtonState.Pressed)
                {
                    try { DragMove(); }
                    catch (InvalidOperationException) { }
                }
            };
            ((Button)layout.FindName("MinimizeButton")).Click += (sender, args) => WindowState = WindowState.Minimized;
            ((Button)layout.FindName("CloseButton")).Click += (sender, args) => CloseFromToolbar();
            refresh.Click += (sender, args) => RefreshAll();
            KeyDown += (sender, args) =>
            {
                if (args.Key == Key.F5)
                    RefreshAll();
            };
            clock.Tick += (sender, args) =>
            {
                UpdateResetLabels();
                codexUpdates.Poll();
                antigravityUpdates.Poll();
                claudeUpdates.Poll();
            };
            SourceInitialized += (sender, args) =>
            {
                var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                source.AddHook(OnWindowMessage);
            };
            Loaded += (sender, args) => StartUpdates();
            Closed += (sender, args) =>
            {
                if (activateWait != null)
                    activateWait.Unregister(null);
                clock.Stop();
                codexUpdates.Dispose();
                antigravityUpdates.Dispose();
                claudeUpdates.Dispose();
                if (tray != null)
                {
                    tray.Visible = false;
                    tray.ContextMenuStrip.Dispose();
                    tray.Dispose();
                    trayIcon.Dispose();
                }
                SaveWindow();
            };
        }

        private static double LimitSize(double requested, double minimum, double maximum, double fallback)
        {
            var size = requested > 0 && !double.IsInfinity(requested) ? requested : fallback;
            return Math.Max(minimum, Math.Min(maximum, size));
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == ExitSizeMove)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(SaveWindow));
            return IntPtr.Zero;
        }

        private void StartUpdates()
        {
            if (updatesStarted)
                return;
            updatesStarted = true;
            RefreshAll();
            clock.Start();
        }

        private void RefreshAll()
        {
            status.Text = "Refreshing quota…";
            codexUpdates.Refresh();
            antigravityUpdates.Refresh();
            claudeUpdates.Refresh();
        }

        private void ShowReading(ProviderCard card, QuotaReading reading)
        {
            if (reading.Pools != null && (card.Latest == null || reading.Updated >= card.Latest.Updated))
                card.Latest = reading;
            card.Render(reading.Problem, resets);
            UpdateResetLabels();
            status.Text = "Checked " + DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void UpdateResetLabels()
        {
            var now = DateTimeOffset.Now;
            foreach (var label in resets)
            {
                var left = label.At - now;
                string display;
                if (left.TotalMinutes <= 0) display = "Reset due";
                else if (left.TotalDays >= 1) display = "in " + left.Days + "d " + left.Hours + "h";
                else if (left.TotalHours >= 1) display = "in " + left.Hours + "h " + left.Minutes + "m";
                else
                {
                    var minutes = (int)Math.Ceiling(left.TotalMinutes);
                    display = minutes >= 60 ? "in 1h 0m" : "in " + minutes + "m";
                }
                label.Text.Text = display;
            }
        }

        private void UpdatePinTooltip()
        {
            pin.ToolTip = Topmost ? "Unpin window" : "Pin above other windows";
        }

        private void CloseFromToolbar()
        {
            if (!closeToTrayOption.IsChecked)
            {
                Close();
                return;
            }
            if (tray == null)
            {
                using (var resource = typeof(Widget).Assembly.GetManifestResourceStream("AIUsageWidget.Icon"))
                    trayIcon = new System.Drawing.Icon(resource, Forms.SystemInformation.SmallIconSize);
                var menu = new Forms.ContextMenuStrip();
                menu.Items.Add("Open AI Usage Widget", null, (sender, args) => RestoreFromTray());
                menu.Items.Add("Exit", null, (sender, args) => Close());
                tray = new Forms.NotifyIcon { Text = "AI Usage Widget", Icon = trayIcon, ContextMenuStrip = menu };
                tray.DoubleClick += (sender, args) => RestoreFromTray();
            }
            tray.Visible = true;
            SaveWindow();
            Hide();
        }

        private void RestoreFromTray()
        {
            if (tray != null)
                tray.Visible = false;
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        }

        private void SaveWindow()
        {
            var bounds = RestoreBounds;
            var useRestoreBounds = WindowState != WindowState.Normal && !bounds.IsEmpty;
            new WidgetSettings
            {
                Pinned = Topmost,
                Left = useRestoreBounds ? bounds.Left : Left,
                Top = useRestoreBounds ? bounds.Top : Top,
                Width = useRestoreBounds ? bounds.Width : ActualWidth,
                Height = SizeToContent == SizeToContent.Manual
                    ? (double?)(useRestoreBounds ? bounds.Height : ActualHeight) : null,
                CodexCollapsed = codex.IsCollapsed,
                AntigravityCollapsed = antigravity.IsCollapsed,
                ClaudeCollapsed = claude.IsCollapsed,
                CloseToTray = closeToTrayOption.IsChecked
            }.Save();
        }

        private sealed class ProviderCard
        {
            public readonly Border Container;
            public QuotaReading Latest;
            private readonly StackPanel body = new StackPanel();
            private readonly TextBlock label;
            private readonly Style quotaBarStyle;
            private readonly ToggleButton toggle;
            private readonly Brush accent;
            private readonly Brush quotaFill;

            public bool IsCollapsed { get { return toggle.IsChecked != true; } }

            public ProviderCard(string name, string accentColor, string headerColor, Style quotaBarStyle,
                Style headerStyle, bool collapsed, Action save, Brush quotaFill = null)
            {
                Container = new Border { BorderBrush = BrushFrom("#272A30") };
                accent = BrushFrom(accentColor);
                this.quotaFill = quotaFill ?? accent;
                this.quotaBarStyle = quotaBarStyle;
                var content = new StackPanel();
                Container.Child = content;
                var heading = new DockPanel();
                label = Text("Connecting…", 10, Muted);
                label.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(label, Dock.Right);
                heading.Children.Add(label);
                var title = Text(name, 13, accent);
                title.FontWeight = FontWeights.SemiBold;
                heading.Children.Add(title);
                toggle = new ToggleButton
                {
                    Style = headerStyle,
                    Content = heading,
                    Background = BrushFrom(headerColor),
                    Foreground = accent,
                    IsChecked = !collapsed
                };
                AutomationProperties.SetName(toggle, "Show or hide " + name + " quota");
                content.Children.Add(toggle);
                body.Margin = new Thickness(28, 0, 14, 8);
                content.Children.Add(body);
                UpdateVisibility();
                toggle.Click += (sender, args) => { UpdateVisibility(); save(); };
            }

            private void UpdateVisibility()
            {
                body.Visibility = IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
                toggle.ToolTip = IsCollapsed ? "Show quota" : "Hide quota";
            }

            public void Render(string problem, List<ResetLabel> resets)
            {
                resets.RemoveAll(reset => reset.Owner == this);
                body.Children.Clear();
                label.Text = problem == null ? "Updated" : Latest == null ? "Unavailable" : "Stale";
                label.Foreground = problem == null ? accent : Amber;
                label.ToolTip = Latest == null ? null : "Last reading " + Latest.Updated.ToLocalTime().ToString("MMM d, HH:mm:ss");
                if (Latest != null)
                {
                    foreach (var pool in Latest.Pools)
                        AddPool(pool, problem != null, resets);
                }
                if (problem != null)
                {
                    var message = Latest == null ? problem :
                        "Last reading " + Latest.Updated.ToLocalTime().ToString("MMM d, HH:mm") + ". " + problem;
                    var warning = Text(message, 11, Muted);
                    warning.Margin = new Thickness(0, 4, 0, 0);
                    warning.TextWrapping = TextWrapping.Wrap;
                    body.Children.Add(warning);
                }
            }

            private void AddPool(QuotaPool pool, bool stale, List<ResetLabel> resets)
            {
                if (!string.IsNullOrEmpty(pool.Label))
                {
                    var group = Text(pool.Label, 11, Muted);
                    group.FontWeight = FontWeights.SemiBold;
                    group.TextWrapping = TextWrapping.Wrap;
                    group.Margin = new Thickness(0, 5, 0, 1);
                    body.Children.Add(group);
                }
                foreach (var window in pool.Windows)
                {
                    body.Children.Add(QuotaRow(pool.Label, window, stale, resets));
                }
            }

            private Grid QuotaRow(string pool, QuotaWindow window, bool stale, List<ResetLabel> resets)
            {
                var row = new Grid { MinHeight = 23 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(59) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(81) });

                var name = Text(window.Label, 11, Muted);
                name.VerticalAlignment = VerticalAlignment.Center;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.ToolTip = window.Label;
                row.Children.Add(name);

                var percentage = FormatPercent(window.Remaining);
                var color = stale ? Muted : window.Remaining <= 10 ? Red : window.Remaining <= 25 ? Amber : quotaFill;
                var gauge = new ProgressBar { Style = quotaBarStyle, Maximum = 100, Value = window.Remaining, Foreground = color };
                Grid.SetColumn(gauge, 1);
                AutomationProperties.SetName(gauge, (pool == null ? "" : pool + " ") + window.Label + " remaining " + percentage);
                row.Children.Add(gauge);

                var remaining = Text(percentage, 12, stale || window.Remaining <= 25 ? color : null);
                remaining.FontFamily = NumericFont;
                remaining.FontWeight = FontWeights.SemiBold;
                remaining.TextAlignment = TextAlignment.Right;
                remaining.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(remaining, 2);
                row.Children.Add(remaining);

                if (window.Reset.HasValue || window.ResetDescription != null)
                {
                    var time = Text(window.Reset.HasValue ? "" : "—", 10, Muted);
                    time.TextAlignment = TextAlignment.Right;
                    time.VerticalAlignment = VerticalAlignment.Center;
                    time.TextTrimming = TextTrimming.CharacterEllipsis;
                    time.Margin = new Thickness(8, 0, 0, 0);
                    time.ToolTip = window.Reset.HasValue
                        ? "Resets " + window.Reset.Value.ToLocalTime().ToString("ddd, MMM d 'at' h:mm tt")
                        : window.ResetDescription;
                    Grid.SetColumn(time, 3);
                    row.Children.Add(time);
                    if (window.Reset.HasValue)
                        resets.Add(new ResetLabel { Text = time, At = window.Reset.Value, Owner = this });
                }
                return row;
            }
        }

        private sealed class ResetLabel
        {
            public DateTimeOffset At;
            public TextBlock Text;
            public ProviderCard Owner;
        }

        private static string FormatPercent(double remaining)
        {
            if (remaining > 0 && remaining < 1) return "<1%";
            if (remaining > 99 && remaining < 100) return ">99%";
            return remaining.ToString("0", CultureInfo.InvariantCulture) + "%";
        }

        private static Brush BrushFrom(string value)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value);
            brush.Freeze();
            return brush;
        }

        private static TextBlock Text(string value, double size, Brush brush)
        {
            var text = new TextBlock { Text = value, FontSize = size };
            if (brush != null)
                text.Foreground = brush;
            return text;
        }
    }

    public sealed class WidgetSettings
    {
#if PORTABLE
        private static readonly string SettingsPath = Path.Combine(
            Path.GetDirectoryName(typeof(WidgetSettings).Assembly.Location), "AI Usage Widget.settings.json");
        private static readonly string LegacySettingsPath = Path.Combine(
            Path.GetDirectoryName(SettingsPath), "Quota Widget.settings.json");
#else
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageWidget", "settings.json");
        private static readonly string LegacySettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaWidget", "settings.json");
#endif
        public bool Pinned { get; set; }
        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public bool CodexCollapsed { get; set; }
        public bool AntigravityCollapsed { get; set; }
        public bool ClaudeCollapsed { get; set; }
        public bool CloseToTray { get; set; }

        public WidgetSettings()
        {
            Pinned = true;
            CloseToTray = true;
        }

        private const int MaxSettingsBytes = 64 * 1024;

        public static WidgetSettings Load()
        {
            try
            {
                var json = new JavaScriptSerializer();
                if (File.Exists(SettingsPath))
                    return json.Deserialize<WidgetSettings>(QuotaSources.ReadBoundedFile(SettingsPath, MaxSettingsBytes, true)) ?? new WidgetSettings();
                if (File.Exists(LegacySettingsPath))
                {
                    var previous = json.Deserialize<WidgetSettings>(QuotaSources.ReadBoundedFile(LegacySettingsPath, MaxSettingsBytes, true));
                    if (previous != null)
                    {
                        previous.Save();
                        return previous;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return new WidgetSettings();
        }

        public void Save()
        {
            string tempPath = null;
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath);
                if (string.IsNullOrEmpty(directory))
                    return;
                Directory.CreateDirectory(directory);
                if (QuotaSources.IsReparsePoint(directory) || QuotaSources.IsReparsePoint(SettingsPath))
                    return;
                var serialized = new JavaScriptSerializer().Serialize(this);
                tempPath = Path.Combine(directory, Path.GetFileName(SettingsPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, System.Text.Encoding.UTF8))
                    writer.Write(serialized);
                if (File.Exists(SettingsPath))
                    File.Replace(tempPath, SettingsPath, null, true);
                else
                    File.Move(tempPath, SettingsPath);
                tempPath = null;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                if (tempPath != null)
                {
                    try { File.Delete(tempPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }
}
