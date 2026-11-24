using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;using IptvPlayer.Engine.ViewModels;

namespace IptvPlayer.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _recordTimer;
    private readonly DispatcherTimer _connectionTimer;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private bool _pipAuto;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        if (Application.Current is { } app) app.SessionEnding += (_, _) => _sessionEnding = true;
        DataContext = vm;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.ReminderRaised += FlashTaskbar;

        // Erotiek wordt weer verborgen zodra je het venster minimaliseert.
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) _vm.AdultLock.Lock(); };

        // Minimaliseren terwijl er iets speelt: het beeld gaat in het zwevende venster door; terugkomen sluit het weer.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                if (_vm.Channels is { Current: not null, IsCasting: false } && !PipWindow.IsOpen && Application.Current is App { Player: { } player })
                {
                    PipWindow.Open(_vm.Channels, player);
                    _pipAuto = PipWindow.IsOpen;
                }
            }
            else if (_pipAuto)
            {
                _pipAuto = false;
                PipWindow.CloseCurrent();
            }
        };

        // Esc moet volledig scherm sluiten, ook als de toetsenbordfocus nergens in de WPF-controls zit
        // (na een klik op de knop, of op het videobeeld/overlay-venster van LibVLC). Daarom hier, op berichtniveau.
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadMessage;

        // Elke halve minuut "nu bezig" verversen.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => _vm.Tick();
        _timer.Start();

        // Een zender die vastloopt of wegvalt wordt snel opnieuw verbonden: elke seconde kijken.
        _connectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _connectionTimer.Tick += (_, _) => _vm.Channels.CheckConnection();
        _connectionTimer.Start();

        // Opnames starten en stoppen op de seconde nauwkeurig genoeg: elke 5 seconden kijken.
        _recordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _recordTimer.Tick += (_, _) => _vm.TickRecordings();
        _recordTimer.Start();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsFullscreen))
            ApplyFullscreen(_vm.IsFullscreen);
    }

    private void ApplyFullscreen(bool fullscreen)
    {
        if (fullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            if (WindowState == WindowState.Normal) _boundsBeforeFullscreen = new Rect(Left, Top, Width, Height);
            // Een gemaximaliseerd venster met eigen vensterkop respecteert de taakbalk. Daarom: normaal venster,
            // exact over de hele monitor, bovenop alles.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            CoverMonitor();
        }
        else
        {
            Topmost = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            if (_stateBeforeFullscreen == WindowState.Normal && _boundsBeforeFullscreen is { } b)
            {
                Left = b.Left; Top = b.Top; Width = b.Width; Height = b.Height;
            }
            WindowState = _stateBeforeFullscreen;
        }

        // De knop waarop je klikte verdwijnt (de bediening wordt verborgen) en neemt de toetsenbordfocus mee;
        // zonder focus komt Esc nooit bij het venster aan. Daarom de focus expliciet terugzetten.
        Dispatcher.BeginInvoke(() =>
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        }, DispatcherPriority.Input);
    }

    private Rect? _boundsBeforeFullscreen;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    /// <summary>Zet het venster precies over de hele monitor waar het nu staat (taakbalk inbegrepen).</summary>
    private void CoverMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (hwnd == IntPtr.Zero || !GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info)) // 2 = dichtstbijzijnde monitor
        {
            WindowState = WindowState.Maximized;
            return;
        }
        var m = info.Monitor;
        // HWND_TOPMOST = -1, SWP_NOACTIVATE | SWP_SHOWWINDOW
        SetWindowPos(hwnd, new IntPtr(-1), m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, 0x0010 | 0x0040);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FlashInfo { public int Size; public IntPtr Hwnd; public int Flags; public int Count; public int Timeout; }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);

    /// <summary>Laat het taakbalkicoon knipperen tot het venster weer vooraan staat (alleen als het venster niet de aandacht heeft).</summary>
    private void FlashTaskbar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || IsActive) return;
        var info = new FlashInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<FlashInfo>(), Hwnd = hwnd, Flags = 0x3 | 0xC, Count = int.MaxValue }; // FLASHW_ALL | FLASHW_TIMERNOFG: knipperen tot het venster weer vooraan staat
        FlashWindowEx(ref info);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            _vm.ToggleFullscreenCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.IsFullscreen)
        {
            _vm.IsFullscreen = false;
            e.Handled = true;
        }
        else if (_vm.IsChannelsActive && !IsTyping(e.OriginalSource))
        {
            // Pauze en spoelen met het toetsenbord (ook in volledig scherm).
            switch (e.Key)
            {
                case Key.Space:
                    _vm.Channels.TogglePauseCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Left when CanSeekWithArrows(e):
                    _vm.Channels.SeekBackCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Right when CanSeekWithArrows(e):
                    _vm.Channels.SeekForwardCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }

    /// <summary>Pijltjestoetsen spoelen alleen als ze niet nodig zijn om door een lijst of tabbladen te lopen.</summary>
    private bool CanSeekWithArrows(KeyEventArgs e) =>
        _vm.Channels.ShowTransport && (_vm.IsFullscreen || e.OriginalSource is not System.Windows.Controls.ListBoxItem);

    /// <summary>Niet de spatiebalk of pijltjes afvangen terwijl iemand in een tekstvak typt of op een knop staat.</summary>
    private static bool IsTyping(object source) =>
        source is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox
            or System.Windows.Controls.Primitives.ButtonBase;

    private const int WmKeyDown = 0x0100;
    private const int VkEscape = 0x1B;

    private void OnThreadMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message != WmKeyDown || (int)msg.wParam != VkEscape || !_vm.IsFullscreen) return;
        _vm.IsFullscreen = false;
        handled = true;
    }

    private bool _sessionEnding;
    private bool _exitConfirmed;

    /// <summary>
    /// Afsluiten terwijl er een opname loopt of gepland staat: eerst vragen of je dat zeker weet, of dat je het venster
    /// alleen wilt minimaliseren (dan gaat het opnemen gewoon door). Bij het afsluiten van Windows vragen we niet.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitConfirmed && !_sessionEnding)
        {
            switch (_vm.AskExit())
            {
                case ExitChoice.Minimize:
                    e.Cancel = true;
                    WindowState = WindowState.Minimized;
                    return;
                case ExitChoice.Stay:
                    e.Cancel = true;
                    return;
            }
            _exitConfirmed = true;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadMessage;
        _timer.Stop();
        _recordTimer.Stop();
        _vm.Downloads.StopAll(); // een lopende download gaat bij de volgende start verder waar het bestand was
        _vm.Recordings.StopAll(); // een lopende opname wordt netjes afgesloten; wat al is opgenomen blijft bewaard
        base.OnClosed(e);
    }
}
