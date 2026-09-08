using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LoopIt7.Views;

namespace LoopIt7;

public partial class App : Application
{
    private const string MutexName = @"Local\LoopIt7.SingleInstance";

    /// <summary>Broadcast by a second launch so the running copy can bring itself forward.</summary>
    internal static readonly int ShowMessage = RegisterWindowMessage("LoopIt7.ShowExistingWindow");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // A second launch is a request to see the window, not to run a second router.
            PostMessage(HwndBroadcast, ShowMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            Shutdown();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--release-cables", StringComparison.OrdinalIgnoreCase)))
        {
            ReleaseCables();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        HonourSystemMotionSetting();

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteCrashLog(ex);
        };

        bool startHidden = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(startHidden);
        MainWindow = window;

        if (!startHidden) window.Show();
        else window.StartHidden();
    }

    /// <summary>
    /// "Show animations in Windows" is the platform's reduced motion switch. When it is off,
    /// every transition in the app collapses to an instant state change.
    /// </summary>
    private void HonourSystemMotionSetting()
    {
        if (SystemParameters.ClientAreaAnimation) return;

        var instant = new Duration(TimeSpan.Zero);
        Resources["MotionFast"] = instant;
        foreach (var dictionary in Resources.MergedDictionaries)
        {
            if (dictionary.Contains("MotionFast")) dictionary["MotionFast"] = instant;
        }
    }

    private bool _reportingError;

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A failure inside the dialog would land right back here. Report the first one only.
        if (_reportingError)
        {
            e.Handled = true;
            return;
        }

        _reportingError = true;
        WriteCrashLog(e.Exception);

        MessageBox.Show(
            $"LoopIt7 hit an unexpected error and has to stop routing.\n\n{e.Exception.Message}",
            "LoopIt7",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        _reportingError = false;
        e.Handled = true;
    }

    /// <summary>
    /// Hands back every cable LoopIt7 renamed, restoring the names Windows had for them. The
    /// uninstaller runs this before it deletes anything.
    /// <para>
    /// Leaving a device called "LoopIt7 Cable" on a machine that no longer has LoopIt7 on it
    /// is the kind of litter nobody can trace afterwards: the name is in Discord's list, in
    /// OBS, in the Sound control panel, and there is nothing left to explain where it came
    /// from. It opens no device and makes no sound.
    /// </para>
    /// </summary>
    private static void ReleaseCables()
    {
        try
        {
            var settingsService = new Services.SettingsService();
            var settings = settingsService.Load();
            if (settings.ClaimedCables.Count == 0) return;

            using var devices = new Audio.DeviceService();
            var endpoints = devices.GetOutputs().Concat(devices.GetInputSources()).ToList();

            Audio.CableOwnership.ReleaseAll(settings, endpoints);
            settingsService.Save(settings);
        }
        catch
        {
            // An uninstall must not stop because a device was already gone. The worst case is
            // a renamed endpoint left behind, and Windows lets anyone rename it back.
        }
    }

    /// <summary>
    /// Writes the headless report to %AppData%\LoopIt7\self-test.txt. Reachable from a
    /// shortcut or a support request without the user having to read anything on screen.
    /// </summary>
    private static void RunSelfTest()
    {
        string directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoopIt7");
        System.IO.Directory.CreateDirectory(directory);

        string report;
        try
        {
            report = Diagnostics.SelfTest.Run();
        }
        catch (Exception ex)
        {
            report = ex.ToString();
        }

        System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "self-test.txt"), report);
    }

    /// <summary>
    /// Appends a crash to %AppData%\LoopIt7\crash.log. Audio devices differ on every machine,
    /// so when something does go wrong this file is the only way to find out what.
    /// </summary>
    internal static void WriteCrashLog(Exception exception)
    {
        try
        {
            string directory = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LoopIt7");
            System.IO.Directory.CreateDirectory(directory);

            string entry = $"""

                === {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===
                {exception}
                """;

            System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "crash.log"), entry);
        }
        catch
        {
            // Nothing useful is left to do if even the log cannot be written.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
