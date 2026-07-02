using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Media.Control;
using Microsoft.Win32;
using NAudio.Wave;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;

namespace AudioVisualizer;

public partial class MainWindow : Window
{
    private const string StartupRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryValueName = "AudioVisualizer";
    private const int GwlExStyle = -20;
    private const int DwmwaCloaked = 14;
    private const int DwmwaExtendedFrameBounds = 9;
    private const uint GwOwner = 4;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const int BandCount = 18;
    private const int SpectrumSampleCount = 1024;
    private const double MinFrequency = 45;
    private const double MaxFrequency = 14000;
    private const double EdgeMargin = 36;
    private const double TaskbarClearance = 0;
    private const double DesktopBottomExtraMargin = 9;
    private const double ActiveAppBottomMargin = 20;
    private const double CollapsedWindowHeight = 92;
    private const double ExpandedWindowHeight = 170;
    private const double MinimumBarScale = 0.08;
    private const double MaximumBarOpacity = 0.92;

    private readonly System.Windows.Threading.DispatcherTimer _avoidTimer;
    private readonly System.Windows.Threading.DispatcherTimer _metadataTimer;
    private readonly System.Windows.Threading.DispatcherTimer _desktopModeTimer;
    private readonly List<Border> _barFills = [];
    private readonly List<ScaleTransform> _barScales = [];
    private readonly Forms.ContextMenuStrip _trayMenu;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly float[] _currentBands = new float[BandCount];
    private readonly float[] _analysisBands = new float[BandCount];
    private readonly float[] _analysisSamples = new float[SpectrumSampleCount];
    private readonly float[] _renderBands = new float[BandCount];
    private readonly float[] _smoothedBands = new float[BandCount];
    private readonly double[] _bandCoefficients = new double[BandCount];
    private readonly double[] _sampleWindow = new double[SpectrumSampleCount];
    private readonly object _gate = new();

    private WasapiLoopbackCapture? _capture;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private float _currentPeak;
    private float _smoothedPeak;
    private int _coefficientSampleRate;
    private int _sampleWindowLength;
    private double _restingLeft;
    private double _restingTop;
    private double _liftedTop;
    private bool _isAvoidingCursor;
    private bool _isDesktopMode;
    private bool _isUpdatingMetadata;
    private IntPtr _windowHandle;
    private string _displayedArtist = "";
    private string _displayedTitle = "";
    private TimeSpan _lastRenderTime;

    public MainWindow()
    {
        InitializeComponent();
        InitializeSpectrumBars();

        _avoidTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _avoidTimer.Tick += AvoidTimer_Tick;

        _metadataTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _metadataTimer.Tick += MetadataTimer_Tick;

        _desktopModeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _desktopModeTimer.Tick += DesktopModeTimer_Tick;

        (_trayMenu, _trayIcon) = CreateTrayIcon();

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        LocationChanged += MainWindow_LocationChanged;
        Closed += MainWindow_Closed;
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
    }

    private (Forms.ContextMenuStrip Menu, Forms.NotifyIcon Icon) CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var startupItem = new Forms.ToolStripMenuItem("Start with Windows")
        {
            Checked = IsStartupEnabled(),
            CheckOnClick = true
        };
        var resetItem = new Forms.ToolStripMenuItem("Reset");
        var exitItem = new Forms.ToolStripMenuItem("Exit");

        startupItem.CheckedChanged += (_, _) => SetStartupEnabled(startupItem.Checked);
        resetItem.Click += (_, _) => Dispatcher.Invoke(RestartApplication);
        exitItem.Click += (_, _) => Dispatcher.Invoke(Close);

        menu.Items.Add(startupItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(resetItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        var icon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = LoadTrayIcon(),
            Text = "Audio Visualizer",
            Visible = true
        };

        icon.DoubleClick += (_, _) => Dispatcher.Invoke(RestartApplication);

        return (menu, icon);
    }

    private static Drawing.Icon LoadTrayIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/tray-icon.ico"));
        if (resource is null)
        {
            return Drawing.SystemIcons.Application;
        }

        using var stream = resource.Stream;
        using var icon = new Drawing.Icon(stream);
        return (Drawing.Icon)icon.Clone();
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKeyPath, false);
        var configuredPath = key?.GetValue(StartupRegistryValueName) as string;
        var executablePath = Environment.ProcessPath;

        return !string.IsNullOrWhiteSpace(configuredPath) &&
            !string.IsNullOrWhiteSpace(executablePath) &&
            string.Equals(NormalizeStartupCommand(configuredPath), executablePath, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKeyPath, true);
        if (key is null)
        {
            return;
        }

        if (!enabled)
        {
            key.DeleteValue(StartupRegistryValueName, false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        key.SetValue(StartupRegistryValueName, $"\"{executablePath}\"");
    }

    private static string NormalizeStartupCommand(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        return value;
    }

    private void InitializeSpectrumBars()
    {
        SpectrumGrid.Children.Clear();
        _barFills.Clear();
        _barScales.Clear();

        for (int i = 0; i < BandCount; i++)
        {
            var scale = new ScaleTransform(1, MinimumBarScale);
            var cell = new Grid
            {
                Margin = new Thickness(2.2, 0, 2.2, 0),
                ClipToBounds = true
            };

            var fill = new Border
            {
                Height = 88,
                MinHeight = 88,
                Opacity = 0.5,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                Background = GetBandBrush(i),
                CornerRadius = new CornerRadius(0),
                RenderTransform = scale,
                RenderTransformOrigin = new WpfPoint(0.5, 1)
            };

            cell.Children.Add(fill);
            SpectrumGrid.Children.Add(cell);
            _barFills.Add(fill);
            _barScales.Add(scale);
        }
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General)
        {
            Dispatcher.Invoke(RefreshSpectrumBarStyle);
        }
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(PositionForCurrentContext);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
    }

    private void RefreshSpectrumBarStyle()
    {
        for (int i = 0; i < _barFills.Count; i++)
        {
            _barFills[i].Background = GetBandBrush(i);
        }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionForCurrentContext();
        Topmost = true;

        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += Capture_DataAvailable;
            _capture.StartRecording();
            CompositionTarget.Rendering += CompositionTarget_Rendering;
            _metadataTimer.Start();
            _desktopModeTimer.Start();
            _ = UpdateMediaMetadataAsync();
        }
        catch (Exception ex)
        {
            Title = $"Không thể bắt âm thanh: {ex.Message}";
        }
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_capture is null) return;

        var sampleCount = ExtractMonoSamples(e.Buffer, e.BytesRecorded, _capture.WaveFormat, _analysisSamples, out var peak);
        AnalyzeSpectrum(_analysisSamples, sampleCount, _capture.WaveFormat.SampleRate, _analysisBands);

        lock (_gate)
        {
            Array.Copy(_analysisBands, _currentBands, BandCount);
            _currentPeak = peak;
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        var renderingArgs = (RenderingEventArgs)e;
        var deltaSeconds = _lastRenderTime == TimeSpan.Zero
            ? 1d / 60d
            : Math.Clamp((renderingArgs.RenderingTime - _lastRenderTime).TotalSeconds, 1d / 240d, 1d / 24d);
        _lastRenderTime = renderingArgs.RenderingTime;

        float peak;

        lock (_gate)
        {
            Array.Copy(_currentBands, _renderBands, BandCount);
            peak = _currentPeak;
        }

        for (int i = 0; i < BandCount; i++)
        {
            var target = Math.Clamp(_renderBands[i], 0f, 1f);
            var attack = target > _smoothedBands[i] ? 18d : 7.5d;
            var blend = 1d - Math.Exp(-attack * deltaSeconds);
            _smoothedBands[i] = (float)(_smoothedBands[i] + ((target - _smoothedBands[i]) * blend));

            var visualLevel = Math.Pow(Math.Clamp(_smoothedBands[i], 0f, 1f), 0.65);
            var scale = MinimumBarScale + (visualLevel * (1d - MinimumBarScale));

            _barScales[i].ScaleY = scale;
            _barFills[i].Opacity = 0.36 + (visualLevel * (MaximumBarOpacity - 0.36));
        }

        var peakBlend = 1d - Math.Exp(-10d * deltaSeconds);
        _smoothedPeak = (float)(_smoothedPeak + ((peak - _smoothedPeak) * peakBlend));
    }

    private async void MetadataTimer_Tick(object? sender, EventArgs e)
    {
        await UpdateMediaMetadataAsync();
    }

    private async Task UpdateMediaMetadataAsync()
    {
        if (_isUpdatingMetadata)
        {
            return;
        }

        _isUpdatingMetadata = true;
        try
        {
            _sessionManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = GetPreferredMediaSession(_sessionManager);
            if (session is null)
            {
                SetMediaText("", "");
                return;
            }

            var properties = await session.TryGetMediaPropertiesAsync();
            var artist = NormalizeMediaText(properties.Artist);
            var title = NormalizeMediaText(properties.Title);

            if (string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
            {
                (artist, title) = SplitTitleAndArtist(title);
            }

            SetMediaText(artist, title);
        }
        catch
        {
            SetMediaText("", "");
        }
        finally
        {
            _isUpdatingMetadata = false;
        }
    }

    private static GlobalSystemMediaTransportControlsSession? GetPreferredMediaSession(
        GlobalSystemMediaTransportControlsSessionManager sessionManager)
    {
        var currentSession = sessionManager.GetCurrentSession();
        if (currentSession is not null && IsPlaying(currentSession))
        {
            return currentSession;
        }

        return sessionManager.GetSessions().FirstOrDefault(IsPlaying) ?? currentSession;
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        return session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    private void SetMediaText(string artist, string title)
    {
        artist = NormalizeMediaText(artist);
        title = NormalizeMediaText(title);

        if (_displayedArtist == artist && _displayedTitle == title)
        {
            return;
        }

        _displayedArtist = artist;
        _displayedTitle = title;

        ArtistText.Text = artist;
        TitleText.Text = title;
        ArtistText.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
        UpdateWindowHeightForContent(true);
    }

    private void UpdateWindowHeightForContent(bool reposition)
    {
        var hasMediaText = ArtistText.Visibility == Visibility.Visible || TitleText.Visibility == Visibility.Visible;
        var mediaTextPanel = GetMediaTextPanel();
        mediaTextPanel.Visibility = hasMediaText && (!_isDesktopMode || _isAvoidingCursor) ? Visibility.Visible : Visibility.Collapsed;

        var targetHeight = mediaTextPanel.Visibility == Visibility.Visible ? ExpandedWindowHeight : CollapsedWindowHeight;
        if (Math.Abs(Height - targetHeight) < 0.5)
        {
            return;
        }

        Height = targetHeight;
        if (reposition)
        {
            PositionForCurrentContext();
        }
    }

    private StackPanel GetMediaTextPanel()
    {
        return (StackPanel)FindName("MediaTextPanel");
    }

    private static string NormalizeMediaText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
    }

    private static (string Artist, string Title) SplitTitleAndArtist(string value)
    {
        var separators = new[] { " - ", " – ", " — " };
        foreach (var separator in separators)
        {
            var parts = value.Split(separator, 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts.All(part => !string.IsNullOrWhiteSpace(part)))
            {
                return (parts[0], parts[1]);
            }
        }

        return ("", value);
    }

    private void DesktopModeTimer_Tick(object? sender, EventArgs e)
    {
        var isDesktopMode = IsDesktopMode();
        if (isDesktopMode == _isDesktopMode)
        {
            return;
        }

        var wasDesktopMode = _isDesktopMode;
        _isDesktopMode = isDesktopMode;
        if (wasDesktopMode && !isDesktopMode)
        {
            PositionForActiveAppContext(ExpandedWindowHeight);
            AnimateToPosition(_restingLeft, _restingTop, () => UpdateWindowHeightForContent(false));
            return;
        }

        PositionForCurrentContext(false);
        AnimateToPosition(_restingLeft, _restingTop);
    }

    private void PositionForCurrentContext(bool applyPosition = true, bool updateHeight = true)
    {
        var placement = GetPrimaryScreenPlacementDip();
        var workArea = placement.SafeArea;
        _isDesktopMode = IsDesktopMode();
        if (updateHeight)
        {
            UpdateWindowHeightForContent(false);
        }
        _restingLeft = _isDesktopMode
            ? workArea.Left + Math.Max(EdgeMargin, (workArea.Width - ActualWidth) / 2d)
            : workArea.Left + EdgeMargin;
        var windowHeight = GetPositioningHeight();
        _restingTop = _isDesktopMode && placement.HasBottomTaskbar
            ? placement.ScreenArea.Bottom - windowHeight - placement.TaskbarArea.Height - DesktopBottomExtraMargin
            : GetActiveAppTop(placement, workArea, windowHeight);
        _liftedTop = Math.Max(workArea.Top + EdgeMargin, _restingTop - windowHeight);

        if (applyPosition)
        {
            Left = _restingLeft;
            Top = _restingTop;
        }
    }

    private double GetDesktopExpandedTop()
    {
        var placement = GetPrimaryScreenPlacementDip();
        return placement.HasBottomTaskbar
            ? placement.ScreenArea.Bottom - ExpandedWindowHeight - placement.TaskbarArea.Height - DesktopBottomExtraMargin
            : placement.SafeArea.Bottom - ExpandedWindowHeight - DesktopBottomExtraMargin;
    }

    private double GetPositioningHeight()
    {
        return double.IsNaN(Height) ? ActualHeight : Height;
    }

    private static double GetActiveAppTop(ScreenPlacement placement, Rect workArea, double windowHeight)
    {
        return placement.HasBottomTaskbar
            ? placement.ScreenArea.Bottom - windowHeight - ActiveAppBottomMargin
            : workArea.Bottom - windowHeight - ActiveAppBottomMargin;
    }

    private void PositionForActiveAppContext(double windowHeight)
    {
        var placement = GetPrimaryScreenPlacementDip();
        var workArea = placement.SafeArea;

        _restingLeft = workArea.Left + EdgeMargin;
        _restingTop = GetActiveAppTop(placement, workArea, windowHeight);
        _liftedTop = Math.Max(workArea.Top + EdgeMargin, _restingTop - windowHeight);
    }

    private ScreenPlacement GetPrimaryScreenPlacementDip()
    {
        var primaryScreen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        var workingArea = primaryScreen.WorkingArea;
        var topLeft = new WpfPoint(workingArea.Left, workingArea.Top);
        var bottomRight = new WpfPoint(workingArea.Right, workingArea.Bottom);
        var screenTopLeft = new WpfPoint(primaryScreen.Bounds.Left, primaryScreen.Bounds.Top);
        var screenBottomRight = new WpfPoint(primaryScreen.Bounds.Right, primaryScreen.Bounds.Bottom);
        var taskbarTopLeft = default(WpfPoint);
        var taskbarBottomRight = default(WpfPoint);
        var hasTaskbar = TryGetTaskbarBounds(out var taskbarBounds);
        var source = PresentationSource.FromVisual(this);

        if (source?.CompositionTarget is not null)
        {
            topLeft = source.CompositionTarget.TransformFromDevice.Transform(topLeft);
            bottomRight = source.CompositionTarget.TransformFromDevice.Transform(bottomRight);
            screenTopLeft = source.CompositionTarget.TransformFromDevice.Transform(screenTopLeft);
            screenBottomRight = source.CompositionTarget.TransformFromDevice.Transform(screenBottomRight);

            if (hasTaskbar)
            {
                taskbarTopLeft = source.CompositionTarget.TransformFromDevice.Transform(
                    new WpfPoint(taskbarBounds.Left, taskbarBounds.Top));
                taskbarBottomRight = source.CompositionTarget.TransformFromDevice.Transform(
                    new WpfPoint(taskbarBounds.Right, taskbarBounds.Bottom));
            }
        }
        else if (hasTaskbar)
        {
            taskbarTopLeft = new WpfPoint(taskbarBounds.Left, taskbarBounds.Top);
            taskbarBottomRight = new WpfPoint(taskbarBounds.Right, taskbarBounds.Bottom);
        }

        var safeArea = new Rect(topLeft, bottomRight);
        var screenArea = new Rect(screenTopLeft, screenBottomRight);
        var taskbarArea = hasTaskbar ? new Rect(taskbarTopLeft, taskbarBottomRight) : Rect.Empty;
        if (hasTaskbar)
        {
            safeArea = ReserveTaskbarArea(safeArea, screenArea, taskbarArea);
        }

        return new ScreenPlacement(safeArea, screenArea, taskbarArea, hasTaskbar);
    }

    private static Rect ReserveTaskbarArea(Rect workArea, Rect screenBounds, Rect taskbarBounds)
    {
        if (!screenBounds.IntersectsWith(taskbarBounds))
        {
            return workArea;
        }

        var bottomGap = Math.Abs(taskbarBounds.Bottom - screenBounds.Bottom);
        var topGap = Math.Abs(taskbarBounds.Top - screenBounds.Top);
        var leftGap = Math.Abs(taskbarBounds.Left - screenBounds.Left);
        var rightGap = Math.Abs(taskbarBounds.Right - screenBounds.Right);
        var minimumGap = Math.Min(Math.Min(bottomGap, topGap), Math.Min(leftGap, rightGap));

        if (minimumGap == bottomGap)
        {
            workArea.Height = Math.Max(0, Math.Min(workArea.Bottom, taskbarBounds.Top - TaskbarClearance) - workArea.Top);
        }
        else if (minimumGap == topGap)
        {
            var top = Math.Max(workArea.Top, taskbarBounds.Bottom + TaskbarClearance);
            workArea.Height = Math.Max(0, workArea.Bottom - top);
            workArea.Y = top;
        }
        else if (minimumGap == leftGap)
        {
            var left = Math.Max(workArea.Left, taskbarBounds.Right + TaskbarClearance);
            workArea.Width = Math.Max(0, workArea.Right - left);
            workArea.X = left;
        }
        else
        {
            workArea.Width = Math.Max(0, Math.Min(workArea.Right, taskbarBounds.Left - TaskbarClearance) - workArea.Left);
        }

        return workArea;
    }

    private static bool TryGetTaskbarBounds(out NativeRect bounds)
    {
        var data = new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>()
        };

        var result = SHAppBarMessage(5, ref data);
        bounds = data.rc;
        return result != IntPtr.Zero && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    private bool IsDesktopMode()
    {
        return IsDesktopForeground() || !HasVisibleApplicationWindowOnPrimaryScreen();
    }

    private bool HasVisibleApplicationWindowOnPrimaryScreen()
    {
        var primaryScreen = Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        var screenBounds = primaryScreen.Bounds;
        var hasVisibleApplicationWindow = false;

        EnumWindows((window, _) =>
        {
            if (!IsApplicationWindow(window, out var bounds) ||
                !Intersects(bounds, screenBounds))
            {
                return true;
            }

            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width < 80 || height < 80)
            {
                return true;
            }

            hasVisibleApplicationWindow = true;
            return false;
        }, IntPtr.Zero);

        return hasVisibleApplicationWindow;
    }

    private bool IsApplicationWindow(IntPtr window, out NativeRect bounds)
    {
        bounds = default;
        if (window == _windowHandle ||
            !IsWindowVisible(window) ||
            IsIconic(window) ||
            IsWindowCloaked(window) ||
            !TryGetVisibleWindowBounds(window, out bounds))
        {
            return false;
        }

        var className = GetWindowClassName(window);
        if (IsShellWindowClass(className))
        {
            return false;
        }

        var exStyle = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        var isToolWindow = (exStyle & WsExToolWindow) != 0;
        var isAppWindow = (exStyle & WsExAppWindow) != 0;
        var owner = GetWindow(window, GwOwner);

        if (isToolWindow || (owner != IntPtr.Zero && !isAppWindow))
        {
            return false;
        }

        return GetWindowTextLength(window) > 0;
    }

    private static bool IsWindowCloaked(IntPtr window)
    {
        var result = DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int));
        return result == 0 && cloaked != 0;
    }

    private static bool IsDesktopForeground()
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero)
        {
            return false;
        }

        if (foregroundWindow == GetShellWindow())
        {
            return true;
        }

        var currentWindow = foregroundWindow;
        for (int i = 0; i < 8 && currentWindow != IntPtr.Zero; i++)
        {
            var className = GetWindowClassName(currentWindow);
            if (IsShellWindowClass(className))
            {
                return true;
            }

            currentWindow = GetParent(currentWindow);
        }

        return false;
    }

    private static bool IsShellWindowClass(string className)
    {
        return className is
            "Progman" or
            "WorkerW" or
            "Shell_TrayWnd" or
            "Shell_SecondaryTrayWnd" or
            "NotifyIconOverflowWindow" or
            "DV2ControlHost" or
            "MsgrIMEWindowClass" or
            "IME";
    }

    private static string GetWindowClassName(IntPtr window)
    {
        var className = new StringBuilder(256);
        return GetClassName(window, className, className.Capacity) == 0
            ? ""
            : className.ToString();
    }

    private static bool TryGetVisibleWindowBounds(IntPtr window, out NativeRect bounds)
    {
        if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<NativeRect>()) != 0)
        {
            return GetWindowRect(window, out bounds) &&
                bounds.Right > bounds.Left &&
                bounds.Bottom > bounds.Top;
        }

        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    private static bool Intersects(NativeRect bounds, Drawing.Rectangle screenBounds)
    {
        return bounds.Left < screenBounds.Right &&
            bounds.Right > screenBounds.Left &&
            bounds.Top < screenBounds.Bottom &&
            bounds.Bottom > screenBounds.Top;
    }

    private void RestartApplication()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            Close();
            return;
        }

        App.ReleaseSingleInstanceMutex();

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true
        });

        Close();
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        Topmost = true;
    }

    private void Window_MouseEnter(object sender, WpfMouseEventArgs e)
    {
        MoveAwayFromCursor();
    }

    private void MoveAwayFromCursor()
    {
        if (_isAvoidingCursor)
        {
            return;
        }

        _isAvoidingCursor = true;
        PositionForCurrentContext(false, false);
        Left = _restingLeft;
        if (_isDesktopMode)
        {
            AnimateTop(GetDesktopExpandedTop(), () => UpdateWindowHeightForContent(false));
        }
        else
        {
            AnimateTop(_liftedTop);
        }
        _avoidTimer.Start();
    }

    private void AvoidTimer_Tick(object? sender, EventArgs e)
    {
        if (IsCursorInProtectedArea())
        {
            return;
        }

        _avoidTimer.Stop();
        if (_isDesktopMode)
        {
            _isAvoidingCursor = false;
            UpdateWindowHeightForContent(false);
            PositionForCurrentContext(false, false);
            AnimateTop(_restingTop);
            return;
        }

        AnimateTop(_restingTop, () =>
        {
            _isAvoidingCursor = false;
            UpdateWindowHeightForContent(true);
        });
    }

    private bool IsCursorInProtectedArea()
    {
        if (!TryGetCursorPositionDip(out var point))
        {
            return false;
        }

        var restingArea = new Rect(_restingLeft, _restingTop, ActualWidth, ActualHeight);
        var currentArea = new Rect(Left, Top, ActualWidth, ActualHeight);

        restingArea.Inflate(10, 10);
        currentArea.Inflate(10, 10);

        return restingArea.Contains(point) || currentArea.Contains(point);
    }

    private bool TryGetCursorPositionDip(out WpfPoint point)
    {
        point = default;
        if (!GetCursorPos(out var cursorPoint))
        {
            return false;
        }

        point = new WpfPoint(cursorPoint.X, cursorPoint.Y);
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not null)
        {
            point = source.CompositionTarget.TransformFromDevice.Transform(point);
        }

        return true;
    }

    private void AnimateTop(double targetTop, Action? completed = null)
    {
        var animation = new DoubleAnimation
        {
            To = targetTop,
            Duration = TimeSpan.FromMilliseconds(190),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        animation.Completed += (_, _) =>
        {
            Top = targetTop;
            completed?.Invoke();
        };

        BeginAnimation(TopProperty, animation);
    }

    private void AnimateToPosition(double targetLeft, double targetTop, Action? completed = null)
    {
        var duration = TimeSpan.FromMilliseconds(240);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var leftAnimation = new DoubleAnimation
        {
            To = targetLeft,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        var topAnimation = new DoubleAnimation
        {
            To = targetTop,
            Duration = duration,
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        topAnimation.Completed += (_, _) =>
        {
            Left = targetLeft;
            Top = targetTop;
            completed?.Invoke();
        };

        BeginAnimation(LeftProperty, leftAnimation);
        BeginAnimation(TopProperty, topAnimation);
    }

    private static int ExtractMonoSamples(byte[] buffer, int bytesRecorded, WaveFormat format, float[] samples, out float peak)
    {
        peak = 0f;
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesRecorded <= 0 || bytesPerSample <= 0 || format.Channels <= 0)
        {
            return 0;
        }

        var bytesPerFrame = bytesPerSample * format.Channels;
        var totalFrames = bytesRecorded / bytesPerFrame;
        if (totalFrames <= 0)
        {
            return 0;
        }

        var frameCount = Math.Min(samples.Length, totalFrames);
        var startFrame = totalFrames - frameCount;

        for (int frame = 0; frame < frameCount; frame++)
        {
            var frameOffset = (startFrame + frame) * bytesPerFrame;
            var sum = 0f;

            for (int channel = 0; channel < format.Channels; channel++)
            {
                var offset = frameOffset + (channel * bytesPerSample);
                sum += ReadSample(buffer, offset, format);
            }

            var sample = sum / format.Channels;
            samples[frame] = sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return frameCount;
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        return format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32 => Math.Clamp(BitConverter.ToSingle(buffer, offset), -1f, 1f),
            WaveFormatEncoding.Pcm when format.BitsPerSample == 16 => BitConverter.ToInt16(buffer, offset) / 32768f,
            WaveFormatEncoding.Pcm when format.BitsPerSample == 8 => (buffer[offset] - 128) / 128f,
            _ => 0f
        };
    }

    private void AnalyzeSpectrum(float[] samples, int sampleCount, int sampleRate, float[] bands)
    {
        Array.Clear(bands, 0, bands.Length);
        if (sampleCount == 0 || sampleRate <= 0)
        {
            return;
        }

        EnsureSpectrumCoefficients(sampleRate);
        EnsureSampleWindow(sampleCount);

        for (int i = 0; i < BandCount; i++)
        {
            var magnitude = CalculateGoertzelMagnitude(samples, sampleCount, _bandCoefficients[i]);

            bands[i] = (float)Math.Clamp(magnitude * 12d, 0d, 1d);
        }
    }

    private void EnsureSpectrumCoefficients(int sampleRate)
    {
        if (_coefficientSampleRate == sampleRate)
        {
            return;
        }

        var maxFrequency = Math.Min(MaxFrequency, sampleRate / 2d);
        var minLog = Math.Log(MinFrequency);
        var maxLog = Math.Log(maxFrequency);

        for (int i = 0; i < BandCount; i++)
        {
            var position = (i + 0.5d) / BandCount;
            var frequency = Math.Exp(minLog + ((maxLog - minLog) * position));
            var normalizedFrequency = frequency / sampleRate;

            _bandCoefficients[i] = 2d * Math.Cos(2d * Math.PI * normalizedFrequency);
        }

        _coefficientSampleRate = sampleRate;
    }

    private void EnsureSampleWindow(int sampleCount)
    {
        if (_sampleWindowLength == sampleCount)
        {
            return;
        }

        if (sampleCount == 1)
        {
            _sampleWindow[0] = 1d;
            _sampleWindowLength = sampleCount;
            return;
        }

        for (int i = 0; i < sampleCount; i++)
        {
            _sampleWindow[i] = 0.5d - (0.5d * Math.Cos((2d * Math.PI * i) / (sampleCount - 1)));
        }

        _sampleWindowLength = sampleCount;
    }

    private double CalculateGoertzelMagnitude(float[] samples, int sampleCount, double coefficient)
    {
        var previous = 0d;
        var previous2 = 0d;

        for (int i = 0; i < sampleCount; i++)
        {
            var value = (samples[i] * _sampleWindow[i]) + (coefficient * previous) - previous2;
            previous2 = previous;
            previous = value;
        }

        var power = (previous2 * previous2) + (previous * previous) - (coefficient * previous * previous2);
        return Math.Sqrt(Math.Max(0d, power)) / sampleCount;
    }

    private static WpfColor GetBandColor(int index)
    {
        var accent = GetWindowsAccentColor();
        var t = index / (double)(BandCount - 1);
        var start = Mix(accent, Colors.White, 0.26);
        var middle = accent;
        var end = Mix(accent, Colors.Black, 0.18);

        if (t < 0.5)
        {
            return Lerp(start, middle, t / 0.5);
        }

        return Lerp(middle, end, (t - 0.5) / 0.5);
    }

    private static WpfBrush GetBandBrush(int index)
    {
        var brush = new SolidColorBrush(Colors.White);
        brush.Freeze();
        return brush;
    }

    private static WpfColor GetWindowsAccentColor()
    {
        var accent = SystemParameters.HighContrast
            ? System.Windows.SystemColors.HighlightColor
            : SystemParameters.WindowGlassColor;

        accent.A = 255;
        var luminance = GetRelativeLuminance(accent);
        if (luminance < 0.22)
        {
            return Mix(accent, Colors.White, 0.34);
        }

        if (luminance > 0.78)
        {
            return Mix(accent, Colors.Black, 0.26);
        }

        return accent;
    }

    private static WpfColor WithAlpha(WpfColor color, byte alpha)
    {
        return WpfColor.FromArgb(alpha, color.R, color.G, color.B);
    }

    private static double GetRelativeLuminance(WpfColor color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.03928
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static WpfColor Lerp(WpfColor start, WpfColor end, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        return WpfColor.FromRgb(
            (byte)(start.R + ((end.R - start.R) * amount)),
            (byte)(start.G + ((end.G - start.G) * amount)),
            (byte)(start.B + ((end.B - start.B) * amount)));
    }

    private static WpfColor Mix(WpfColor color, WpfColor target, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        return WpfColor.FromRgb(
            (byte)(color.R + ((target.R - color.R) * amount)),
            (byte)(color.G + ((target.G - color.G) * amount)),
            (byte)(color.B + ((target.B - color.B) * amount)));
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _avoidTimer.Stop();
        _metadataTimer.Stop();
        _desktopModeTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _capture?.StopRecording();
        _capture?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref AppBarData pData);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private readonly struct ScreenPlacement(Rect safeArea, Rect screenArea, Rect taskbarArea, bool hasTaskbar)
    {
        public Rect SafeArea { get; } = safeArea;
        public Rect ScreenArea { get; } = screenArea;
        public Rect TaskbarArea { get; } = taskbarArea;
        public bool HasBottomTaskbar { get; } = hasTaskbar
            && screenArea.IntersectsWith(taskbarArea)
            && Math.Abs(taskbarArea.Bottom - screenArea.Bottom) <= 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public NativeRect rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
