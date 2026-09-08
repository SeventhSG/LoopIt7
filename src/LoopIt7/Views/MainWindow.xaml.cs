using System.Windows;
using System.Windows.Interop;
using System.Windows.Forms;
using LoopIt7.Services;
using LoopIt7.ViewModels;
using Icon = System.Drawing.Icon;
using GdiColor = System.Drawing.Color;
using GdiSize = System.Drawing.Size;

namespace LoopIt7.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly HotkeyService _hotkey = new();
    private readonly NotifyIcon _tray = new();

    private ToolStripMenuItem? _trayRouting;
    private bool _startHidden;
    private bool _reallyClosing;

    public MainWindow(bool startHidden)
    {
        InitializeComponent();

        _startHidden = startHidden;
        _viewModel = new MainViewModel(Dispatcher);
        DataContext = _viewModel;

        Width = _viewModel.WindowWidth;
        Height = _viewModel.WindowHeight;

        _viewModel.HotkeyPreferenceChanged += (_, _) => SyncHotkey();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        SizeChanged += OnSizeChanged;
    }

    /// <summary>Launched with --tray: create the window handle but never show the window.</summary>
    public void StartHidden()
    {
        // A hidden window still needs a handle for the global hotkey and the broadcast message.
        new WindowInteropHelper(this).EnsureHandle();
        OnLoaded(this, new RoutedEventArgs());
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        WindowEffects.ApplyDarkMica(this);

        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is { } source)
        {
            source.AddHook(WndProc);
        }

        _hotkey.Attach(this);
        _hotkey.Pressed += (_, _) => _viewModel.ToggleAllSourcesMuted();
        SyncHotkey();

        SetUpTray();
        _viewModel.StartIfConfigured();

        if (_startHidden || _viewModel.StartMinimized)
        {
            _startHidden = false;
            Hide();
        }
    }

    // Tray

    private void SetUpTray()
    {
        _tray.Text = "LoopIt7";
        _tray.Icon = LoadTrayIcon();
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left) RestoreWindow();
        };

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new TrayMenuColors()),
            BackColor = GdiColor.FromArgb(0x22, 0x1F, 0x1A),
            ForeColor = GdiColor.FromArgb(0xED, 0xE9, 0xE3),
            ShowImageMargin = false
        };

        var open = new ToolStripMenuItem("Open LoopIt7", null, (_, _) => RestoreWindow());
        _trayRouting = new ToolStripMenuItem("Start routing", null, (_, _) => _viewModel.ToggleRoutingCommand.Execute(null));
        var mute = new ToolStripMenuItem("Mute all sources", null, (_, _) => _viewModel.ToggleAllSourcesMuted());
        var quit = new ToolStripMenuItem("Quit", null, (_, _) => ShutDown());

        menu.Items.AddRange([open, new ToolStripSeparator(), _trayRouting, mute, new ToolStripSeparator(), quit]);
        menu.Opening += (_, _) => SyncTrayMenu();

        _tray.ContextMenuStrip = menu;
        SyncTrayMenu();
    }

    private static Icon LoadTrayIcon()
    {
        var stream = Application.GetResourceStream(new Uri("Assets/LoopIt7.ico", UriKind.Relative))?.Stream;
        return stream is not null
            ? new Icon(stream, new GdiSize(16, 16))
            : System.Drawing.SystemIcons.Application;
    }

    private void SyncTrayMenu()
    {
        if (_trayRouting is not null) _trayRouting.Text = _viewModel.IsRunning ? "Stop routing" : "Start routing";

        // The tray tooltip has a 63 character limit, and Windows silently drops longer ones.
        string text = _viewModel.IsRunning ? $"LoopIt7 - {_viewModel.StatusText}" : "LoopIt7 - stopped";
        _tray.Text = text.Length <= 63 ? text : text[..63];
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.StatusText))
        {
            SyncTrayMenu();
        }
    }

    private void SyncHotkey()
    {
        if (_viewModel.MuteHotkeyEnabled) _hotkey.Register(_viewModel.HotkeyModifiers, _viewModel.HotkeyVirtualKey);
        else _hotkey.Unregister();
    }

    // Window plumbing

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == App.ShowMessage)
        {
            RestoreWindow();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // A borderless window bleeds past the work area when maximised. Pull the content back in.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        MaximiseGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
        MaximiseButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (WindowState != WindowState.Normal) return;
        _viewModel.WindowWidth = Width;
        _viewModel.WindowHeight = Height;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClosing || !_viewModel.CloseToTray) return;

        // Routing keeps running while the window is away. That is the point of a router.
        e.Cancel = true;
        Hide();
    }

    private void ShutDown()
    {
        _reallyClosing = true;
        _viewModel.SaveNow();
        _tray.Visible = false;
        _tray.Dispose();
        _hotkey.Dispose();
        _viewModel.Dispose();
        Application.Current.Shutdown();
    }

    // Handlers

    private void OnMinimiseClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CloseToTray) Hide();
        else ShutDown();
    }

    private void OnAddSourceClick(object sender, RoutedEventArgs e)
    {
        // Which programs are playing changes minute to minute, so the list is built on open.
        _viewModel.RefreshApplicationsCommand.Execute(null);
        AddOutputPopup.IsOpen = false;
        AddSourcePopup.IsOpen = !AddSourcePopup.IsOpen;
    }

    private void OnAddOutputClick(object sender, RoutedEventArgs e)
    {
        AddSourcePopup.IsOpen = false;
        AddOutputPopup.IsOpen = !AddOutputPopup.IsOpen;
    }

    private void OnAddVirtualOutputClick(object sender, RoutedEventArgs e)
    {
        AddSourcePopup.IsOpen = false;
        AddOutputPopup.IsOpen = false;
        AddVirtualOutputPopup.IsOpen = !AddVirtualOutputPopup.IsOpen;

        if (AddVirtualOutputPopup.IsOpen) VirtualOutputNameBox.Focus();
    }

    private void OnVirtualOutputNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;

        CreateVirtualOutput();
        e.Handled = true;
    }

    private void OnCreateVirtualOutputClick(object sender, RoutedEventArgs e) => CreateVirtualOutput();

    private void CreateVirtualOutput()
    {
        if (!_viewModel.CreateVirtualOutputCommand.CanExecute(null)) return;

        _viewModel.CreateVirtualOutputCommand.Execute(null);
        AddVirtualOutputPopup.IsOpen = false;
    }

    private void OnMenuRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;

        if ((string?)button.Tag == "destination") _viewModel.AddDestinationCommand.Execute(button.DataContext);
        else _viewModel.AddSourceCommand.Execute(button.DataContext);

        AddSourcePopup.IsOpen = false;
        AddOutputPopup.IsOpen = false;
    }

    private void OnPresetsClick(object sender, RoutedEventArgs e) => PresetsPopup.IsOpen = !PresetsPopup.IsOpen;

    private void OnOptionsClick(object sender, RoutedEventArgs e) => OptionsPopup.IsOpen = !OptionsPopup.IsOpen;
}

/// <summary>Dark colours for the tray menu, so it matches the window it belongs to.</summary>
internal sealed class TrayMenuColors : ProfessionalColorTable
{
    private static readonly GdiColor Surface = GdiColor.FromArgb(0x22, 0x1F, 0x1A);
    private static readonly GdiColor Hover = GdiColor.FromArgb(0x33, 0x2E, 0x27);
    private static readonly GdiColor Line = GdiColor.FromArgb(0x3C, 0x35, 0x2E);
    private static readonly GdiColor Check = GdiColor.FromArgb(0x3B, 0x2E, 0x19);

    public override GdiColor ToolStripDropDownBackground => Surface;
    public override GdiColor MenuItemSelected => Hover;
    public override GdiColor MenuItemSelectedGradientBegin => Hover;
    public override GdiColor MenuItemSelectedGradientEnd => Hover;
    public override GdiColor MenuItemBorder => Hover;
    public override GdiColor MenuBorder => Line;
    public override GdiColor ImageMarginGradientBegin => Surface;
    public override GdiColor ImageMarginGradientMiddle => Surface;
    public override GdiColor ImageMarginGradientEnd => Surface;
    public override GdiColor SeparatorDark => Line;
    public override GdiColor SeparatorLight => Line;
    public override GdiColor CheckBackground => Check;
    public override GdiColor CheckSelectedBackground => Check;
}
