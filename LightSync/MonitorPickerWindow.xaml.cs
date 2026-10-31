using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightSync.Capture;

namespace LightSync;

/// <summary>One card in the display picker: a live snapshot of the monitor plus its name.</summary>
public sealed class MonitorChoice
{
    const double ThumbHeightDip = 170;
    const double MaxThumbWidthDip = 340;

    public required MonitorInfo Monitor { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public ImageSource? Thumbnail { get; init; }
    public double ThumbWidth { get; init; }
    public double ThumbHeight { get; init; }
    public Visibility PrimaryVisibility => Monitor.Primary ? Visibility.Visible : Visibility.Collapsed;
    public Brush Ring { get; init; } = Brushes.Transparent;

    public static MonitorChoice Create(MonitorInfo monitor, bool isCurrent, string? position)
    {
        double aspect = monitor.Bounds.Width / (double)Math.Max(1, monitor.Bounds.Height);
        double width = Math.Min(MaxThumbWidthDip, ThumbHeightDip * aspect);
        var detail = $"{monitor.Bounds.Width} × {monitor.Bounds.Height}";
        if (position != null) detail += $"  ·  {position}";
        if (isCurrent) detail += "  ·  current";
        return new MonitorChoice
        {
            Monitor = monitor,
            Title = Monitors.ShortName(monitor.DeviceName),
            Detail = detail,
            Thumbnail = Snapshot(monitor),
            ThumbWidth = width,
            ThumbHeight = width / aspect,
            Ring = isCurrent ? new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x58)) : Brushes.Transparent,
        };
    }

    static BitmapSource? Snapshot(MonitorInfo monitor)
    {
        try
        {
            using var capture = new GdiCapture(monitor.Bounds, sampleWidth: 640);
            var frame = new CaptureFrame();
            capture.TryCapture(0, frame);
            var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Pixels, frame.Width * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null; // e.g. secure desktop; the card shows "Preview unavailable"
        }
    }
}

/// <summary>Shows a snapshot of every monitor so the user can pick the one to follow by sight.</summary>
public partial class MonitorPickerWindow : Window
{
    public MonitorPickerWindow(IReadOnlyList<MonitorInfo> monitors, string? currentDeviceName, bool offerRemember)
    {
        InitializeComponent();
        RememberBox.Visibility = offerRemember ? Visibility.Visible : Visibility.Collapsed;

        // Left-to-right in the same order they're arranged in Windows display settings.
        var ordered = monitors.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
        MonitorList.ItemsSource = ordered
            .Select(m => MonitorChoice.Create(m, string.Equals(m.DeviceName, currentDeviceName, StringComparison.OrdinalIgnoreCase), Position(m, ordered)))
            .ToList();
    }

    public string? SelectedDeviceName { get; private set; }
    public bool Remember => RememberBox.IsChecked == true;

    /// <summary>Shows the picker; owned by <paramref name="owner"/> when it's on screen, otherwise centered and on top.</summary>
    public bool ShowFor(Window? owner)
    {
        if (owner is { IsVisible: true })
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = true;
        }
        return ShowDialog() == true;
    }

    void Monitor_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MonitorChoice choice) return;
        SelectedDeviceName = choice.Monitor.DeviceName;
        DialogResult = true;
    }

    /// <summary>"Left" / "Right" / "Middle" for screens arranged side by side; "Top" / "Bottom" for stacked ones.</summary>
    static string? Position(MonitorInfo monitor, List<MonitorInfo> ordered)
    {
        if (ordered.Count < 2) return null;
        bool sideBySide = ordered.Select(m => m.Bounds.X).Distinct().Count() == ordered.Count;
        if (sideBySide)
        {
            int index = ordered.IndexOf(monitor);
            return index == 0 ? "left" : index == ordered.Count - 1 ? "right" : "middle";
        }
        bool stacked = ordered.Select(m => m.Bounds.Y).Distinct().Count() == ordered.Count;
        if (stacked)
        {
            var byY = ordered.OrderBy(m => m.Bounds.Y).ToList();
            int index = byY.IndexOf(monitor);
            return index == 0 ? "top" : index == byY.Count - 1 ? "bottom" : "middle";
        }
        return null;
    }
}
