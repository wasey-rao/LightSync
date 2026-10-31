using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LightSync.Ble;
using LightSync.Capture;
using LightSync.Sync;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LightSync;

public sealed record Option(object Value, string Label);

public sealed record EffectItem(LightEffect Effect, Brush Swatch);

public sealed record ScanResult(ulong Address, string Name, short Rssi, bool Connected = false)
{
    public string Mac => BleAddress.Format(Address);
    public string DisplayName => string.IsNullOrEmpty(Name) ? "(unnamed)" : Name;
    public string SignalText => Connected ? "connected" : $"{Rssi} dBm";
}

public partial class MainWindow : Window
{
    const string AppIconUri = "pack://application:,,,/Assets/LightSync.ico";
    const int MaxLogLines = 300;

    static readonly (string Name, string Hex)[] Presets =
    [
        ("Red", "#FF0000"), ("Orange", "#FF4D00"), ("Amber", "#FF9900"), ("Yellow", "#FFE100"),
        ("Lime", "#80FF00"), ("Green", "#00FF1E"), ("Mint", "#00FF99"), ("Cyan", "#00E5FF"),
        ("Sky", "#0088FF"), ("Blue", "#0010FF"), ("Violet", "#6A00FF"), ("Purple", "#B400FF"),
        ("Magenta", "#FF00D0"), ("Pink", "#FF2266"), ("Warm white", "#FFB060"), ("White", "#FFFFFF"),
    ];

    static readonly Color IdlePreviewColor = Color.FromRgb(0x20, 0x20, 0x26);

    readonly AppSettings _settings;
    readonly ElkBledomDevice _device = new();
    readonly ScreenSyncEngine _engine;
    readonly Forms.NotifyIcon _tray;
    readonly DispatcherTimer _saveTimer;
    readonly SolidColorBrush _previewBrush = new(IdlePreviewColor);
    readonly ObservableCollection<ScanResult> _scanResults = [];
    readonly Queue<string> _logLines = new();
    string _savedJson;
    bool _ready, _exiting, _trayHintShown, _poweredOff, _updatingColorUi;
    double _colorValue = 1; // HSV value of the static color; the hue/saturation sliders keep it
    SyncFrame? _latestFrame;
    int _previewQueued;
    CancellationTokenSource? _scanCancel;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        _engine = new ScreenSyncEngine(_device, () => _settings.Sync);
        ApplyIdleRelease();
        _engine.FrameProcessed += OnFrameProcessed;
        _device.StateChanged += () => Dispatcher.InvokeAsync(UpdateConnectionUi);
        AppLog.LineAdded += line => Dispatcher.InvokeAsync(() => AppendLog(line));

        LogoImage.Source = LoadIconFrame(64);
        PreviewSwatch.Background = _previewBrush;
        PopulateChoices();
        BuildSwatches();
        BuildEffects();
        ScanList.ItemsSource = _scanResults;
        StartWithWindowsBox.IsChecked = StartupManager.IsEnabled;

        PageSync.DataContext = _settings.Sync;
        PageColor.DataContext = _settings;
        PageEffects.DataContext = _settings;
        PageSettings.DataContext = _settings;
        ShowStaticColor(_settings.StaticColor);

        _tray = CreateTrayIcon();
        _savedJson = _settings.Serialize();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) => SaveSettings();
        _saveTimer.Start();

        UpdateConnectionUi();
        UpdateSyncUi();
        _ready = true;

        AppLog.Write("LightSync started.");
        if (_settings.AutoConnect) ConnectToConfiguredDevice(showErrors: false);
        if (_settings.ResumeSyncOnLaunch && _settings.SyncWasRunning) StartSync();
    }

    // ───────────── Window & tray ─────────────

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_exiting)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (_settings.CloseToTray)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(4000, "LightSync is still running",
                    "It keeps controlling your strip from the system tray. Right-click the tray icon to exit.", Forms.ToolTipIcon.Info);
            }
        }
        else
        {
            Dispatcher.InvokeAsync(ExitApp);
        }
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _scanCancel?.Cancel();

        bool syncing = _engine.IsRunning;
        _engine.Dispose();
        _settings.SyncWasRunning = syncing;
        _saveTimer.Stop();
        SaveSettings();

        _device.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    Forms.NotifyIcon CreateTrayIcon()
    {
        var syncItem = new Forms.ToolStripMenuItem("Start screen sync", null, (_, _) => ToggleSync());
        var openItem = new Forms.ToolStripMenuItem("Open LightSync", null, (_, _) => ShowFromTray());
        var menu = new Forms.ContextMenuStrip();
        openItem.Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold);
        menu.Items.Add(openItem);
        menu.Items.Add(syncItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Turn on", null, (_, _) => PowerOn());
        menu.Items.Add("Turn off", null, (_, _) => PowerOff());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        menu.Opening += (_, _) => syncItem.Text = _engine.IsRunning ? "Stop screen sync" : "Start screen sync";

        var resource = Application.GetResourceStream(new Uri(AppIconUri))!;
        using var iconStream = resource.Stream;
        var tray = new Forms.NotifyIcon
        {
            Icon = new Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize),
            Text = "LightSync",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowFromTray(); };
        return tray;
    }

    static BitmapFrame LoadIconFrame(int minSize)
    {
        var decoder = new IconBitmapDecoder(new Uri(AppIconUri), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.Where(f => f.PixelWidth >= minSize).OrderBy(f => f.PixelWidth).FirstOrDefault()
            ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageSync == null) return; // raised during InitializeComponent
        PageSync.Visibility = NavSync.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageColor.Visibility = NavColor.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageEffects.Visibility = NavEffects.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void SaveSettings()
    {
        var json = _settings.Serialize();
        if (json == _savedJson) return;
        AppSettings.Save(json);
        _savedJson = json;
    }

    // ───────────── Connection & power ─────────────

    void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_device.WantsConnection)
        {
            StopSync(restoreColor: false);
            _device.Disconnect();
        }
        else
        {
            ConnectToConfiguredDevice(showErrors: true);
        }
        UpdateConnectionUi();
    }

    bool ConnectToConfiguredDevice(bool showErrors)
    {
        if (!BleAddress.TryParse(_settings.DeviceAddress, out ulong address))
        {
            if (showErrors)
                MessageBox.Show("Enter the strip's Bluetooth address on the Settings page, for example BE:58:94:02:AF:91.",
                    "LightSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _device.Connect(address);
        return true;
    }

    void EnsureConnected()
    {
        if (!_device.WantsConnection) ConnectToConfiguredDevice(showErrors: true);
    }

    void UpdateConnectionUi()
    {
        bool wanted = _device.WantsConnection;
        var (dot, title) = _device.State switch
        {
            LinkState.Connected => ("#22C55E", "Connected"),
            LinkState.Connecting => ("#F59E0B", "Connecting"),
            LinkState.Searching => ("#F59E0B", "Searching"),
            LinkState.Standby => ("#60A5FA", "Standby"),
            _ => wanted ? ("#EF4444", "Not reachable") : ("#6B7280", "Disconnected"),
        };
        StatusDot.Fill = BrushFrom(dot);
        StatusTitle.Text = title;
        StatusDetail.Text = wanted || _device.State != LinkState.Disconnected ? _device.Status : "Click Connect to link your strip.";
        ConnectButton.Content = wanted ? "Disconnect" : "Connect";
        if (_tray != null) _tray.Text = $"LightSync · {title}";
        UpdateLinkWarning();
    }

    /// <summary>Makes it obvious when screen sync is running but nothing reaches the strip.</summary>
    void UpdateLinkWarning()
    {
        bool show = _engine.IsRunning && _device.State != LinkState.Connected;
        LinkWarning.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        if (_device.State is LinkState.Connecting or LinkState.Searching or LinkState.Standby)
        {
            LinkWarningTitle.Text = "Connecting to your strip…";
            LinkWarningDetail.Text = "Colors start flowing as soon as it's connected. If this keeps repeating, the strip is connected to " +
                "another device: exit LightSync on your other PC (tray icon → Exit) or close Lotus Lantern on your phone.";
        }
        else
        {
            LinkWarningTitle.Text = "Colors aren't reaching your strip";
            LinkWarningDetail.Text = _device.Status + " LightSync keeps retrying and connects as soon as the strip is free.";
        }
    }

    void ApplyIdleRelease() => _device.IdleReleaseMs = _settings.ReleaseWhenIdle ? 120_000 : null;

    void ReleaseWhenIdle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _settings.ReleaseWhenIdle = ReleaseWhenIdleBox.IsChecked == true;
        ApplyIdleRelease();
    }

    void PowerOn_Click(object sender, RoutedEventArgs e) => PowerOn();

    void PowerOff_Click(object sender, RoutedEventArgs e) => PowerOff();

    void PowerOn()
    {
        EnsureConnected();
        _poweredOff = false;
        _device.Send(ElkProtocol.PowerOn(), "power");
    }

    void PowerOff()
    {
        StopSync(restoreColor: false);
        EnsureConnected();
        _poweredOff = true;
        _device.Send(ElkProtocol.PowerOff(), "power", replacesColor: true);
    }

    void EnsurePoweredOn()
    {
        if (_poweredOff) PowerOn();
    }

    // ───────────── Screen sync ─────────────

    void PopulateChoices()
    {
        RefreshMonitors();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            RefreshMonitors();
            PageSync.DataContext = null;
            PageSync.DataContext = _settings.Sync;
        });

        ModeBox.ItemsSource = new[]
        {
            new Option(SyncMode.Vibrant, "Vibrant: colorful highlights"),
            new Option(SyncMode.Average, "Average: whole-screen mix"),
            new Option(SyncMode.Dominant, "Dominant: strongest color"),
        };
        RegionBox.ItemsSource = new[]
        {
            new Option(CaptureRegion.Full, "Whole screen"),
            new Option(CaptureRegion.Edges, "Edges (ambilight style)"),
            new Option(CaptureRegion.Center, "Center"),
        };
        CaptureBox.ItemsSource = new[]
        {
            new Option(CaptureMethod.Auto, "Automatic (recommended)"),
            new Option(CaptureMethod.DirectX, "DirectX: games and video"),
            new Option(CaptureMethod.Gdi, "GDI: compatibility mode"),
        };
    }

    /// <summary>Reloads the monitor list (screens can be plugged in or removed while running).</summary>
    void RefreshMonitors()
    {
        var monitors = Monitors.GetAll();
        MonitorBox.ItemsSource = monitors;
        if (!monitors.Any(m => m.DeviceName == _settings.Sync.Monitor))
            _settings.Sync.Monitor = Monitors.Resolve(null).DeviceName;

        var multiple = monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ChooseDisplayButton.Visibility = multiple;
        AskDisplayBox.Visibility = multiple;
    }

    void ChooseDisplay_Click(object sender, RoutedEventArgs e) => PickDisplay(offerRemember: false);

    /// <summary>Shows the visual display picker. Returns false if the user cancelled.</summary>
    bool PickDisplay(bool offerRemember)
    {
        var picker = new MonitorPickerWindow(Monitors.GetAll(), _settings.Sync.Monitor, offerRemember);
        if (!picker.ShowFor(this) || picker.SelectedDeviceName == null) return false;

        _settings.Sync.Monitor = picker.SelectedDeviceName;
        if (picker.Remember) _settings.Sync.AskForDisplay = false;
        PageSync.DataContext = null;
        PageSync.DataContext = _settings.Sync;
        AppLog.Write($"Following {Monitors.ShortName(picker.SelectedDeviceName)}.");
        return true;
    }

    void SyncButton_Click(object sender, RoutedEventArgs e) => ToggleSync();

    void ToggleSync()
    {
        if (_engine.IsRunning) StopSync();
        else StartSync(interactive: true);
    }

    /// <param name="interactive">True when the user started sync by hand (may ask which screen to follow).</param>
    void StartSync(bool interactive = false)
    {
        if (_engine.IsRunning) return;
        if (interactive && _settings.Sync.AskForDisplay && Monitors.GetAll().Count > 1 && !PickDisplay(offerRemember: true))
            return;
        EnsureConnected();
        _poweredOff = false;
        _device.Send(ElkProtocol.PowerOn(), "power");
        _device.Send(ElkProtocol.Brightness((int)Math.Round(_settings.Brightness)), "brightness");
        _engine.Start();
        _settings.SyncWasRunning = true;
        UpdateSyncUi();
    }

    void StopSync(bool restoreColor = true)
    {
        if (!_engine.IsRunning) return;
        _engine.Stop();
        _settings.SyncWasRunning = false;
        if (restoreColor && ParseColor(_settings.StaticColor) is { } color)
            _device.SetColor(color.R, color.G, color.B);
        UpdateSyncUi();
    }

    void UpdateSyncUi()
    {
        bool running = _engine.IsRunning;
        SyncButtonText.Text = running ? "Stop Screen Sync" : "Start Screen Sync";
        SyncButtonIcon.Text = running ? "" : "";
        _previewBrush.Color = IdlePreviewColor;
        PreviewGlow.Opacity = 0;
        PreviewHex.Foreground = PreviewStats.Foreground = (Brush)FindResource("TextPrimary");
        PreviewHex.Text = running ? "Starting…" : "Idle";
        PreviewStats.Text = running ? "Waiting for the first frame" : "Press Start to mirror your screen onto the strip.";
        UpdateLinkWarning();
    }

    void OnFrameProcessed(SyncFrame frame)
    {
        // Called ~30×/s on the sync thread: keep at most one UI update queued.
        _latestFrame = frame;
        if (Interlocked.Exchange(ref _previewQueued, 1) == 1) return;
        Dispatcher.InvokeAsync(() =>
        {
            Volatile.Write(ref _previewQueued, 0);
            if (_latestFrame is { } latest && _engine.IsRunning && IsVisible) ShowPreview(latest);
        }, DispatcherPriority.Render);
    }

    void ShowPreview(SyncFrame frame)
    {
        var color = Color.FromRgb(frame.R, frame.G, frame.B);
        _previewBrush.Color = color;
        PreviewGlow.Color = color;
        PreviewGlow.Opacity = 0.9;
        PreviewHex.Text = ToHex(color);
        PreviewStats.Text = $"{frame.Source} capture  ·  {frame.Fps:0} fps  ·  {frame.ProcessMs:0.0} ms per frame";
        var text = Luminance(color) > 0.55 ? Brushes.Black : Brushes.White;
        PreviewHex.Foreground = text;
        PreviewStats.Foreground = text;
    }

    void ResetSync_Click(object sender, RoutedEventArgs e)
    {
        var current = _settings.Sync;
        _settings.Sync = new SyncSettings
        {
            Monitor = current.Monitor,
            AskForDisplay = current.AskForDisplay,
            Capture = current.Capture,
            Mode = current.Mode,
            Region = current.Region,
            CropBlackBars = current.CropBlackBars,
            GainR = current.GainR,
            GainG = current.GainG,
            GainB = current.GainB,
        };
        PageSync.DataContext = _settings.Sync;
    }

    void ResetWhiteBalance_Click(object sender, RoutedEventArgs e)
    {
        _settings.Sync.GainR = _settings.Sync.GainG = _settings.Sync.GainB = 1;
        PageSync.DataContext = null;
        PageSync.DataContext = _settings.Sync;
    }

    // ───────────── Static color ─────────────

    void BuildSwatches()
    {
        foreach (var (name, hex) in Presets)
        {
            var swatch = new Button { Style = (Style)FindResource("SwatchButton"), Background = BrushFrom(hex), ToolTip = name };
            AutomationProperties.SetName(swatch, name);
            swatch.Click += (_, _) => SetStaticColor(hex);
            SwatchPanel.Children.Add(swatch);
        }
    }

    /// <summary>Updates the color page to show <paramref name="hex"/> without sending anything.</summary>
    void ShowStaticColor(string hex)
    {
        var color = ParseColor(hex) ?? Color.FromRgb(0xB4, 0x00, 0xFF);
        var (h, s, v) = ToHsv(color);
        _updatingColorUi = true;
        HueSlider.Value = h;
        SatSlider.Value = s * 100;
        _updatingColorUi = false;
        _colorValue = v;
        UpdateColorVisuals(color);
    }

    void SetStaticColor(string hex)
    {
        if (ParseColor(hex) is not { } color) return;
        ShowStaticColor(hex);
        ApplyStaticColor(color);
    }

    void Hsv_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingColorUi || !_ready) return;
        if (_colorValue < 0.05) _colorValue = 1;
        var color = FromHsv(HueSlider.Value, SatSlider.Value / 100, _colorValue);
        UpdateColorVisuals(color);
        ApplyStaticColor(color);
    }

    void ApplyStaticColor(Color color)
    {
        _settings.StaticColor = ToHex(color);
        StopSync(restoreColor: false);
        EnsureConnected();
        EnsurePoweredOn();
        _device.SetColor(color.R, color.G, color.B);
    }

    void UpdateColorVisuals(Color color)
    {
        ColorPreview.Background = new SolidColorBrush(color);
        ColorGlow.Color = color;
        if (!HexBox.IsKeyboardFocused) HexBox.Text = ToHex(color);
        SatSlider.Background = new LinearGradientBrush(Colors.White, FromHsv(HueSlider.Value, 1, 1), 0);
    }

    void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitHex();
        e.Handled = true;
    }

    void HexBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitHex();

    void CommitHex()
    {
        var text = HexBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (ParseColor(text) is { } color && !string.Equals(ToHex(color), _settings.StaticColor, StringComparison.OrdinalIgnoreCase))
            SetStaticColor(ToHex(color));
        else
            HexBox.Text = _settings.StaticColor;
    }

    void BrightnessSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (!_ready) return;
        EnsureConnected();
        _device.Send(ElkProtocol.Brightness((int)Math.Round(BrightnessSlider.Value)), "brightness");
    }

    // ───────────── Effects ─────────────

    void BuildEffects()
    {
        List<EffectItem> Items(EffectKind kind) =>
            ElkProtocol.Effects.Where(e => e.Kind == kind).Select(e => new EffectItem(e, GradientFrom(e.Colors))).ToList();
        FadeEffects.ItemsSource = Items(EffectKind.Fade);
        JumpEffects.ItemsSource = Items(EffectKind.Jump);
        BlinkEffects.ItemsSource = Items(EffectKind.Blink);
    }

    void Effect_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LightEffect effect) return;
        StopSync(restoreColor: false);
        EnsureConnected();
        EnsurePoweredOn();
        _device.Send(ElkProtocol.Effect(effect.Code), "effect", replacesColor: true);
        _device.Send(ElkProtocol.EffectSpeed((int)Math.Round(_settings.EffectSpeed)), "speed");
        AppLog.Write($"Effect: {effect.Name}");
    }

    void SpeedSlider_ValueChanged(object? sender, EventArgs e)
    {
        if (!_ready) return;
        EnsureConnected();
        _device.Send(ElkProtocol.EffectSpeed((int)Math.Round(SpeedSlider.Value)), "speed");
    }

    // ───────────── Settings page ─────────────

    void UseAddress_Click(object sender, RoutedEventArgs e)
    {
        if (!BleAddress.TryParse(_settings.DeviceAddress, out ulong address))
        {
            MessageBox.Show("That doesn't look like a Bluetooth address. Expected something like BE:58:94:02:AF:91.",
                "LightSync", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UseDevice(address);
    }

    void UseDevice(ulong address)
    {
        _settings.DeviceAddress = BleAddress.Format(address);
        AddressBox.Text = _settings.DeviceAddress;
        _device.Connect(address);
        UpdateConnectionUi();
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancel != null)
        {
            _scanCancel.Cancel();
            return;
        }
        _scanResults.Clear();
        // A connected strip stops advertising, so it would never show up in the scan by itself.
        if (_device.State == LinkState.Connected && BleAddress.TryParse(_settings.DeviceAddress, out ulong connected))
            _scanResults.Add(new ScanResult(connected, "ELK-BLEDOM", 0, Connected: true));
        ScanList.Visibility = Visibility.Visible;
        ScanButtonText.Text = "Stop";
        _scanCancel = new CancellationTokenSource();
        try
        {
            await ElkBledomDevice.ScanAsync(TimeSpan.FromSeconds(15),
                (address, name, rssi) => Dispatcher.InvokeAsync(() => AddScanResult(address, name, rssi)), _scanCancel.Token);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Scan failed: {ex.Message}");
        }
        finally
        {
            _scanCancel.Dispose();
            _scanCancel = null;
            ScanButtonText.Text = "Scan";
        }
    }

    void AddScanResult(ulong address, string name, short rssi)
    {
        for (int i = 0; i < _scanResults.Count; i++)
        {
            var existing = _scanResults[i];
            if (existing.Address != address) continue;
            // Only replace on meaningful changes so the list (and selection) doesn't flicker.
            if (Math.Abs(existing.Rssi - rssi) >= 4 || (name.Length > 0 && name != existing.Name))
                _scanResults[i] = existing with { Name = name.Length > 0 ? name : existing.Name, Rssi = rssi };
            return;
        }
        if (name.Length == 0) return; // unnamed advertisers are phones, beacons, etc.
        _scanResults.Add(new ScanResult(address, name, rssi));
    }

    void ScanList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ScanList.SelectedItem is ScanResult result)
        {
            _scanCancel?.Cancel();
            UseDevice(result.Address);
        }
    }

    void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        try
        {
            StartupManager.SetEnabled(StartWithWindowsBox.IsChecked == true);
            AppLog.Write(StartWithWindowsBox.IsChecked == true ? "LightSync will start with Windows." : "LightSync will no longer start with Windows.");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Could not change the startup setting: {ex.Message}");
        }
    }

    void OpenLogFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });

    void AppendLog(string line)
    {
        _logLines.Enqueue(line);
        if (_logLines.Count > MaxLogLines + 100)
        {
            while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
            LogBox.Text = string.Join(Environment.NewLine, _logLines) + Environment.NewLine;
        }
        else
        {
            LogBox.AppendText(line + Environment.NewLine);
        }
        LogBox.ScrollToEnd();
    }

    // ───────────── Color helpers ─────────────

    static SolidColorBrush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush(ParseColor(hex) ?? Colors.Gray);
        brush.Freeze();
        return brush;
    }

    static LinearGradientBrush GradientFrom(string[] hexColors)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i < hexColors.Length; i++)
            brush.GradientStops.Add(new GradientStop(ParseColor(hexColors[i]) ?? Colors.Gray,
                hexColors.Length == 1 ? 0 : (double)i / (hexColors.Length - 1)));
        brush.Freeze();
        return brush;
    }

    static Color? ParseColor(string? hex)
    {
        if (hex == null || !Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")) return null;
        return Color.FromRgb(Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16));
    }

    static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * ((g - b) / d % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
