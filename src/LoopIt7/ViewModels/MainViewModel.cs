using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using LoopIt7.Audio;
using LoopIt7.Audio.Graph;
using LoopIt7.Models;
using LoopIt7.Services;

namespace LoopIt7.ViewModels;

public enum WorkspaceTab
{
    Patchbay,
    Devices,
    Midi
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Meter refresh. Fast enough to read a transient, slow enough to stay free.</summary>
    private static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>Windows fires several endpoint notifications per plug event. Coalesce them.</summary>
    private static readonly TimeSpan DeviceSettleTime = TimeSpan.FromMilliseconds(400);

    /// <summary>How often to reopen anything that is waiting on a device or a program.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(3);

    private const double LaneSourceX = 48;

    /// <summary>Virtual outputs sit between the two lanes, because that is what they are.</summary>
    private const double LaneVirtualX = 356;

    private const double LaneDestinationX = 664;
    private const double LaneTop = 32;
    /// <summary>Card height plus a gap, so auto placed boxes never touch.</summary>
    private const double LaneSpacing = PatchNodeViewModel.NodeHeight + 26;

    private readonly Dispatcher _dispatcher;
    private readonly DeviceService _devices;
    private readonly AudioGraph _graph;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _meterTimer;
    private readonly DispatcherTimer _deviceTimer;
    private readonly DispatcherTimer _retryTimer;
    private readonly DispatcherTimer _noticeTimer;

    private bool _loading = true;
    private bool _disposed;

    private WorkspaceTab _tab = WorkspaceTab.Patchbay;
    private bool _isRunning;
    private int _latencyMs = 10;
    private string _statusText = "Nothing patched yet";
    private string? _notice;
    private string _newPresetName = string.Empty;
    private CableViewModel? _selectedCable;
    private int _liveNodeCount;
    private string _newVirtualOutputName = string.Empty;

    /// <summary>The cable carrying LoopIt7's own name, and the one that could be asked to.</summary>
    private CableInlet? _ownCable;
    private CableInlet? _adoptableCable;

    /// <summary>
    /// Programs we have muted in the Windows volume mixer, and the process id we muted, so
    /// every one of them can be handed back no matter how the app is closed.
    /// </summary>
    private readonly Dictionary<string, int> _silencedApps = [];

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _settingsService = new SettingsService();
        _settings = _settingsService.Load();

        _devices = new DeviceService();
        _graph = new AudioGraph(_devices);
        _graph.NodeFailed += OnNodeFailed;
        _devices.DevicesChanged += OnDevicesChanged;

        Midi = new MidiViewModel(Persist);

        ToggleRoutingCommand = new RelayCommand(_ => ToggleRouting());
        AddSourceCommand = new RelayCommand(AddSourceFromDescriptor);
        AddDestinationCommand = new RelayCommand(AddDestinationFromDescriptor);
        CreateVirtualOutputCommand = new RelayCommand(_ => CreateVirtualOutput(), _ => CanCreateVirtualOutput);
        RefreshApplicationsCommand = new RelayCommand(_ => RefreshApplications());
        RefreshDevicesCommand = new RelayCommand(_ => { RefreshDevices(); RebuildDeviceRows(); });
        ClearPatchbayCommand = new RelayCommand(_ => ClearPatchbay());
        ClearSoloCommand = new RelayCommand(_ => ClearSolo());
        ApplyTemplateCommand = new RelayCommand(p => ApplyTemplate(p as string ?? "monitoring"));
        SavePresetCommand = new RelayCommand(_ => SavePreset(), _ => CanSavePreset);
        LoadPresetCommand = new RelayCommand(p => LoadPreset(p as PatchPreset));
        DeletePresetCommand = new RelayCommand(p => DeletePreset(p as PatchPreset));
        OpenSettingsFolderCommand = new RelayCommand(_ => OpenSettingsFolder());
        OpenCableSiteCommand = new RelayCommand(_ => OpenUrl(VirtualCableService.RecommendedCableUrl));
        AdoptCableCommand = new RelayCommand(_ => AdoptCable(), _ => CanAdoptCable);
        ReleaseCableCommand = new RelayCommand(_ => ReleaseCable(), _ => HasOwnCable);
        OpenProjectCommand = new RelayCommand(_ => OpenUrl(ProjectUrl));
        SelectTabCommand = new RelayCommand(p =>
        {
            if (p is WorkspaceTab tab) Tab = tab;
        });

        foreach (var preset in _settings.Presets) Presets.Add(preset);

        RefreshDevices();
        RefreshApplications();
        RebuildDeviceRows();
        RestoreWorkspace(_settings.Nodes, _settings.Cables);
        ReleaseLeftoverTakeovers();

        Midi.ApplySavedRoutes(_settings.MidiRoutes.Select(r => (r.Input, r.Output)));

        _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = MeterInterval };
        _meterTimer.Tick += OnMeterTick;

        _deviceTimer = new DispatcherTimer { Interval = DeviceSettleTime };
        _deviceTimer.Tick += OnDeviceSettleTick;

        _retryTimer = new DispatcherTimer { Interval = RetryInterval };
        _retryTimer.Tick += OnRetryTick;

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); Notice = null; };

        Presets.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPresets));

        _latencyMs = _settings.LatencyMs is 3 or 5 or 10 or 20 or 40 ? _settings.LatencyMs : 10;
        _graph.BufferMilliseconds = _latencyMs;
        _settings.StartWithWindows = StartupService.IsEnabled();

        if (Enum.TryParse(_settings.LastTab, out WorkspaceTab savedTab)) _tab = savedTab;

        _loading = false;
        UpdateStatus();
    }

    // Collections

    public ObservableCollection<SourceNodeViewModel> Sources { get; } = [];

    /// <summary>The virtual outputs: boxes that take cables in and send the sum out again.</summary>
    public ObservableCollection<VirtualOutputNodeViewModel> VirtualOutputs { get; } = [];

    public ObservableCollection<DestinationNodeViewModel> Destinations { get; } = [];
    public ObservableCollection<CableViewModel> Cables { get; } = [];
    public ObservableCollection<PatchPreset> Presets { get; } = [];

    /// <summary>Every possible source endpoint: real inputs plus every output tapped in loopback.</summary>
    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = [];

    /// <summary>Just the recording endpoints, for the "Inputs" half of the add menu.</summary>
    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = [];

    /// <summary>Playback endpoints offered as loopback taps.</summary>
    public ObservableCollection<AudioDeviceInfo> LoopbackDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = [];

    /// <summary>
    /// The ways in to LoopIt7 this machine offers, one per cable end pairing. Kept as its own
    /// list because the picker that uses it lives inside a node's card, where the main view
    /// model is out of reach, exactly like the application list.
    /// </summary>
    public ObservableCollection<CableInlet> CableInlets { get; } = [];
    public ObservableCollection<AudioApplication> Applications { get; } = [];
    public ObservableCollection<DeviceRowViewModel> DeviceRows { get; } = [];

    public MidiViewModel Midi { get; }

    public IReadOnlyList<int> LatencyOptions { get; } = [3, 5, 10, 20, 40];

    // Commands

    public RelayCommand ToggleRoutingCommand { get; }
    public RelayCommand AddSourceCommand { get; }
    public RelayCommand AddDestinationCommand { get; }
    public RelayCommand CreateVirtualOutputCommand { get; }
    public RelayCommand RefreshApplicationsCommand { get; }
    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand ClearPatchbayCommand { get; }
    public RelayCommand ClearSoloCommand { get; }
    public RelayCommand ApplyTemplateCommand { get; }
    public RelayCommand SavePresetCommand { get; }
    public RelayCommand LoadPresetCommand { get; }
    public RelayCommand DeletePresetCommand { get; }
    public RelayCommand OpenSettingsFolderCommand { get; }
    public RelayCommand OpenCableSiteCommand { get; }
    public RelayCommand AdoptCableCommand { get; }
    public RelayCommand ReleaseCableCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand SelectTabCommand { get; }

    public const string ProjectUrl = "https://github.com/SeventhSG/LoopIt7";

    public string AuthorText => "Built by SeventhSG";

    // State

    public WorkspaceTab Tab
    {
        get => _tab;
        set
        {
            if (!SetProperty(ref _tab, value)) return;
            OnPropertyChanged(nameof(IsPatchbayTab));
            OnPropertyChanged(nameof(IsDevicesTab));
            OnPropertyChanged(nameof(IsMidiTab));

            _settings.LastTab = value.ToString();
            Persist();
        }
    }

    public bool IsPatchbayTab => _tab == WorkspaceTab.Patchbay;
    public bool IsDevicesTab => _tab == WorkspaceTab.Devices;
    public bool IsMidiTab => _tab == WorkspaceTab.Midi;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(RoutingButtonText));
        }
    }

    public string RoutingButtonText => IsRunning ? "Stop routing" : "Start routing";

    public bool HasNodes => Sources.Count > 0 || Destinations.Count > 0 || VirtualOutputs.Count > 0;

    /// <summary>Name typed into the new virtual output field.</summary>
    public string NewVirtualOutputName
    {
        get => _newVirtualOutputName;
        set
        {
            if (SetProperty(ref _newVirtualOutputName, value)) OnPropertyChanged(nameof(CanCreateVirtualOutput));
        }
    }

    public bool CanCreateVirtualOutput => !string.IsNullOrWhiteSpace(_newVirtualOutputName);

    public bool HasVirtualCable => OutputDevices.Any(VirtualCableService.IsVirtual);

    /// <summary>What other programs see in their own device list when they want to reach LoopIt7.</summary>
    public string? OwnCableName => _ownCable?.Feed.Name;

    public bool HasOwnCable => _ownCable is not null;

    /// <summary>The cable LoopIt7 would rename if asked, named so the offer says which one.</summary>
    public string? AdoptableCableName => _adoptableCable?.Feed.Name;

    /// <summary>
    /// Only offered while LoopIt7 has no cable of its own. One way in is what the app needs,
    /// and a second name in every program's list would be a choice nobody asked to make.
    /// </summary>
    public bool CanAdoptCable => _adoptableCable is not null && _ownCable is null;

    public string RecommendedCableName => VirtualCableService.RecommendedCableName;

    public int LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (!SetProperty(ref _latencyMs, value)) return;
            _graph.BufferMilliseconds = value;
            if (_loading) return;
            if (IsRunning) RestartRouting();
            Persist();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (!SetProperty(ref _notice, value)) return;
            OnPropertyChanged(nameof(HasNotice));

            _noticeTimer?.Stop();
            if (!string.IsNullOrEmpty(value)) _noticeTimer?.Start();
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public CableViewModel? SelectedCable
    {
        get => _selectedCable;
        set
        {
            if (_selectedCable is not null) _selectedCable.IsSelected = false;
            if (!SetProperty(ref _selectedCable, value)) return;
            if (_selectedCable is not null) _selectedCable.IsSelected = true;
            OnPropertyChanged(nameof(HasSelectedCable));
        }
    }

    public bool HasSelectedCable => _selectedCable is not null;

    public string NewPresetName
    {
        get => _newPresetName;
        set
        {
            if (SetProperty(ref _newPresetName, value)) OnPropertyChanged(nameof(CanSavePreset));
        }
    }

    public bool CanSavePreset => !string.IsNullOrWhiteSpace(_newPresetName);

    public bool HasPresets => Presets.Count > 0;

    public string VersionText =>
        $"Version {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"}";

    // Options

    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value) return;
            _settings.StartWithWindows = value && StartupService.SetEnabled(true);
            if (!value) StartupService.SetEnabled(false);
            OnPropertyChanged();
            Persist();
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set { _settings.StartMinimized = value; OnPropertyChanged(); Persist(); }
    }

    public bool AutoStartRouting
    {
        get => _settings.AutoStartRouting;
        set { _settings.AutoStartRouting = value; OnPropertyChanged(); Persist(); }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set { _settings.CloseToTray = value; OnPropertyChanged(); Persist(); }
    }

    public bool MuteHotkeyEnabled
    {
        get => _settings.MuteHotkeyEnabled;
        set
        {
            _settings.MuteHotkeyEnabled = value;
            OnPropertyChanged();
            Persist();
            HotkeyPreferenceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public uint HotkeyModifiers => (uint)_settings.MuteHotkeyModifiers;
    public uint HotkeyVirtualKey => (uint)_settings.MuteHotkeyKey;
    public string HotkeyText => "Ctrl + Alt + M";

    public event EventHandler? HotkeyPreferenceChanged;

    public double WindowWidth
    {
        get => _settings.WindowWidth;
        set { _settings.WindowWidth = value; Persist(); }
    }

    public double WindowHeight
    {
        get => _settings.WindowHeight;
        set { _settings.WindowHeight = value; Persist(); }
    }

    // Transport

    public void StartIfConfigured()
    {
        if (AutoStartRouting && Cables.Count > 0) StartRouting();
    }

    /// <summary>Mutes every source at once. This is what the global hotkey reaches.</summary>
    public void ToggleAllSourcesMuted()
    {
        bool anyAudible = Sources.Any(s => !s.Muted);
        foreach (var source in Sources) source.Muted = anyAudible;
        Notice = anyAudible ? "All sources muted." : "Sources unmuted.";
    }

    private void ToggleRouting()
    {
        if (IsRunning) StopRouting();
        else StartRouting();
    }

    private void StartRouting()
    {
        if (Sources.Count == 0)
        {
            Notice = "Add a source first.";
            return;
        }

        if (Destinations.Count == 0)
        {
            Notice = "Add an output first.";
            return;
        }

        if (Cables.Count == 0)
        {
            Notice = "Drag a cable from a source to an output.";
            return;
        }

        _graph.BufferMilliseconds = LatencyMs;

        if (!_graph.Start(out string? error))
        {
            Notice = error ?? "Routing could not start.";
            return;
        }

        Notice = null;
        IsRunning = true;
        _meterTimer.Start();
        _retryTimer.Start();

        foreach (var source in Sources) ApplyExclusive(source);

        UpdateStatus();
    }

    private void StopRouting()
    {
        _graph.Stop();
        _meterTimer.Stop();
        _retryTimer.Stop();
        IsRunning = false;
        _liveNodeCount = 0;

        // Anything we took off its own output gets it back the moment routing stops. Leaving
        // a program muted in the volume mixer is the kind of thing people never find again.
        ReleaseAllExclusive();

        foreach (var node in AllNodes())
        {
            node.Peak = 0;
            node.ApplyStatus(NodeStatus.Idle, null, 0, 0);
        }

        foreach (var cable in Cables) cable.Peak = 0;

        UpdateStatus();
    }

    private void RestartRouting()
    {
        StopRouting();
        StartRouting();
    }

    // Building the patchbay

    private IEnumerable<PatchNodeViewModel> AllNodes() =>
        Sources.Cast<PatchNodeViewModel>().Concat(VirtualOutputs).Concat(Destinations);

    private void AddSourceFromDescriptor(object? descriptor)
    {
        switch (descriptor)
        {
            case AudioDeviceInfo device when device.Kind == AudioSourceKind.Capture:
                AddDeviceSource(device, loopback: false);
                break;
            case AudioDeviceInfo device:
                AddDeviceSource(device, loopback: true);
                break;
            case AudioApplication application:
                AddApplicationSource(application);
                break;
            case CableInlet inlet:
                AddVirtualInput(inlet);
                break;
        }
    }

    /// <summary>
    /// The one sentence a box fed by a cable needs. Written once because it is the whole
    /// instruction: everything else about a cable is invisible from the other program.
    /// </summary>
    private static string InletHintFor(AudioDeviceInfo feed) =>
        $"Choose \"{feed.Name}\" as the output in the program that should play into LoopIt7. It arrives here.";

    /// <summary>
    /// Puts the way in from Windows on the canvas as a box of its own: the other program sends
    /// to the cable's playback end, LoopIt7 listens on its recording end, and what arrives can
    /// be patched anywhere a microphone could.
    /// <para>
    /// This is the half of the cable a DAW needs. A virtual output is a box other programs play
    /// into on their way somewhere else; this is the same trick with nothing bolted on top,
    /// which is what somebody who only wants their DAW heard inside LoopIt7 is after.
    /// </para>
    /// </summary>
    public SourceNodeViewModel AddVirtualInput(CableInlet inlet)
    {
        var existing = Sources.FirstOrDefault(s =>
            s.Kind == SourceKind.Device &&
            string.Equals(s.DeviceId, inlet.Pickup.Id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            Notice = $"{existing.Title} is already on the canvas. {existing.InletHint ?? InletHintFor(inlet.Feed)}";
            return existing;
        }

        var created = AddDeviceSource(inlet.Pickup, loopback: false);
        Notice = created.InletHint;
        return created;
    }

    private void AddDestinationFromDescriptor(object? descriptor)
    {
        if (descriptor is AudioDeviceInfo device) AddDestination(device);
    }

    private SourceNodeViewModel AddDeviceSource(AudioDeviceInfo device, bool loopback, NodeSettings? saved = null)
    {
        var kind = loopback ? SourceKind.DeviceLoopback : SourceKind.Device;

        // A cable's recording end is not a microphone. It is the way in from the rest of
        // Windows: something else on this machine was told to play into the other end of the
        // same cable. The box reads as that other end, because its name is the only part of
        // this the user ever sees anywhere else.
        var feed = !loopback && VirtualCableService.IsVirtual(device)
            ? VirtualCableService.FindFeedEndpoints(device, OutputDevices).FirstOrDefault()
            : null;

        string title = saved?.Title ?? feed?.Name ?? device.Name;
        string subtitle = saved?.Subtitle ?? (loopback ? "system audio" : feed is not null ? "virtual input" : device.InterfaceName);

        var vm = new SourceNodeViewModel(saved?.Id ?? NewId(), title, subtitle, kind, device.Id, 0, string.Empty)
        {
            IsVirtualInput = feed is not null,
            InletBadge = feed is null || string.Equals(title, feed.Name, StringComparison.Ordinal)
                ? null
                : $"via {feed.Name}",
            InletHint = feed is null ? null : InletHintFor(feed)
        };

        PlaceNode(vm, saved, LaneSourceX, Sources.Count);
        WireNode(vm);

        Sources.Add(vm);
        _graph.AddDeviceSource(vm.Id, title, device.Id, loopback);
        PushMix(vm);

        Finish();
        return vm;
    }

    private SourceNodeViewModel AddApplicationSource(AudioApplication application, NodeSettings? saved = null)
    {
        string title = saved?.Title ?? application.DisplayName;
        var vm = new SourceNodeViewModel(
            saved?.Id ?? NewId(), title, "application",
            SourceKind.Application, string.Empty, application.ProcessId, application.ExecutableName);

        PlaceNode(vm, saved, LaneSourceX, Sources.Count);
        WireNode(vm);

        Sources.Add(vm);
        _graph.AddApplicationSource(vm.Id, title, application.ProcessId, application.ExecutableName);
        PushMix(vm);

        Finish();
        return vm;
    }

    /// <summary>
    /// Creates a virtual output. It is a box inside LoopIt7, not a Windows endpoint: nothing
    /// else on the machine can select it, and programs reach it by being captured into it.
    /// </summary>
    public VirtualOutputNodeViewModel AddVirtualOutput(string name, NodeSettings? saved = null)
    {
        var vm = new VirtualOutputNodeViewModel(saved?.Id ?? NewId(), saved?.Title ?? name)
        {
            Applications = Applications,
            Inlets = CableInlets,
            InletFeedDeviceId = saved?.InletFeedDeviceId ?? string.Empty
        };
        PlaceNode(vm, saved, LaneVirtualX, VirtualOutputs.Count);
        WireNode(vm);

        VirtualOutputs.Add(vm);
        _graph.AddVirtualOutput(vm.Id, vm.Title);
        PushMix(vm);

        Finish();
        return vm;
    }

    private void CreateVirtualOutput()
    {
        string name = NewVirtualOutputName.Trim();
        if (name.Length == 0) return;

        var created = AddVirtualOutput(name);
        NewVirtualOutputName = string.Empty;

        Notice = $"\"{created.Title}\" is ready. Assign a program to it, then run cables out to every output it should reach.";
    }

    /// <summary>
    /// Points one program at a virtual output: captures it if it is not on the canvas yet,
    /// patches it in, and takes it off its own output so it is heard once rather than twice.
    /// </summary>
    public void AssignApplication(VirtualOutputNodeViewModel target, AudioApplication application)
    {
        var existing = Sources.FirstOrDefault(s =>
            s.Kind == SourceKind.Application &&
            string.Equals(s.ExecutableName, application.ExecutableName, StringComparison.OrdinalIgnoreCase));

        var source = existing ?? AddApplicationSource(application);

        if (Cables.Any(c => c.Source == source && c.Destination == target))
        {
            Notice = $"{source.Title} already feeds {target.Title}.";
            return;
        }

        if (!TryConnect(source, target)) return;

        source.Exclusive = true;
        Notice = $"{source.Title} now plays through {target.Title} only. Send {target.Title} to every output that should hear it.";
    }

    /// <summary>
    /// Gives a virtual output a way in from the rest of Windows, so other programs can send
    /// audio to it instead of only being captured into it.
    /// <para>
    /// A cable has a playback end and a recording end. The other program picks the playback
    /// end as its output, LoopIt7 listens on the recording end, and what arrives lands in this
    /// box and leaves down whatever cables the box already has. That is the whole trick, and
    /// it is why a virtual output can look like a device to Discord without LoopIt7 shipping a
    /// driver of its own.
    /// </para>
    /// <para>
    /// If the cable is one LoopIt7 installed, both of its ends are renamed after the box, so
    /// the name in Discord's list is the name on the canvas. A cable that was already on the
    /// machine keeps the name it came with, because somebody else's OBS scene may be pointing
    /// at it.
    /// </para>
    /// </summary>
    public bool TryPublishVirtualOutput(
        VirtualOutputNodeViewModel target,
        CableInlet inlet,
        out string? error)
    {
        var feed = inlet.Feed;
        var pickup = inlet.Pickup;

        if (!VirtualCableService.IsVirtual(feed))
        {
            Notice = error = $"{feed.Name} is a real output, not a cable. Only a cable has a recording end for LoopIt7 to listen on.";
            return false;
        }

        var taken = VirtualOutputs.FirstOrDefault(box =>
            box != target &&
            string.Equals(box.InletFeedDeviceId, feed.Id, StringComparison.OrdinalIgnoreCase));

        if (taken is not null)
        {
            // A cable carries one stream. Pointed at two boxes it would deliver the same audio
            // to both, and there would be two boxes claiming to be the same Windows device.
            Notice = error = $"{feed.Name} is already the way in to {taken.Title}. A cable carries one thing at a time, so give {target.Title} a different one, or install another cable.";
            return false;
        }

        var source = Sources.FirstOrDefault(s =>
                         s.Kind == SourceKind.Device &&
                         string.Equals(s.DeviceId, pickup.Id, StringComparison.OrdinalIgnoreCase))
                     ?? AddDeviceSource(pickup, loopback: false);

        if (!Cables.Any(c => c.Source == source && c.Destination == target) &&
            !TryConnect(source, target))
        {
            // TryConnect has already put its own reason on screen, and that reason is more
            // specific than anything worth writing here.
            Notice ??= $"{pickup.Name} could not be patched into {target.Title}.";
            error = Notice;
            return false;
        }

        // The cable takes the box's name, so the word in Discord's list and the word on the
        // canvas are the same one. A cable LoopIt7 has already named is renamed in place: it
        // has been called "LoopIt7 Cable" since the day it was installed, and claiming it a
        // second time would lose the names it originally came with.
        if (CableOwnership.MayClaim(_settings, feed))
        {
            bool renamed = CableOwnership.FindClaim(_settings, feed) is { } claim
                ? CableOwnership.TryRename(_settings, claim, target.Title, OutputDevices.Concat(InputDevices), out _)
                : CableOwnership.TryClaim(_settings, feed, pickup, target.Title, out _);

            if (renamed)
            {
                _settingsService.Save(_settings);
                RefreshDevices();

                // Both ends answer to something else now. Say the new names rather than the
                // ones the picker was showing a moment ago.
                feed = OutputDevices.FirstOrDefault(d => d.Id == feed.Id) ?? feed;
                pickup = InputDevices.FirstOrDefault(
                    d => d.Id == pickup.Id && d.Kind == AudioSourceKind.Capture) ?? pickup;

                if (source.IsVirtualInput)
                {
                    source.Title = feed.Name;
                    source.InletBadge = $"via {feed.Name}";
                    source.InletHint = InletHintFor(feed);
                }
            }
        }

        target.InletFeedDeviceId = feed.Id;
        target.InletHint = $"Choose \"{feed.Name}\" as the output in any program that should play through {target.Title}. LoopIt7 listens on \"{pickup.Name}\".";
        target.InletBadge = $"via {feed.Name}";
        Persist();

        Notice = $"{target.Title} is reachable from Windows now. {target.InletHint}";
        error = null;
        return true;
    }

    /// <summary>
    /// Hands a cable that was already on this machine to LoopIt7, because the user asked for
    /// it in as many words.
    /// <para>
    /// Nothing automatic ever does this. A cable that was here first is named in somebody's
    /// OBS scene or Discord settings, and renaming one behind their back breaks a setup we did
    /// not build. But a machine that already has VB-CABLE never gets one from our installer,
    /// so without this the person most likely to want LoopIt7's own name is the one who can
    /// never have it. The offer says which device it will rename, and the uninstaller puts the
    /// old name back.
    /// </para>
    /// </summary>
    public void AdoptCable()
    {
        if (_adoptableCable is not { } inlet) return;

        string was = inlet.Feed.Name;
        string name = CableOwnership.DefaultNameFor(_settings);

        CableOwnership.Adopt(_settings, inlet.Feed, inlet.Pickup);

        if (!CableOwnership.TryClaim(_settings, inlet.Feed, inlet.Pickup, name, out string? error))
        {
            Notice = $"Could not rename {was}: {error}";
            return;
        }

        _settingsService.Save(_settings);
        RefreshDevices();
        RebuildDeviceRows();

        Notice = $"{was} is called \"{name}\" now. Pick that as the output in any program that should play into LoopIt7. Uninstalling LoopIt7 puts the old name back.";
    }

    /// <summary>
    /// Gives the cable its old name back, for good. Marked as released so nothing here names
    /// it again on the next device refresh, which is what "give it back" has to mean.
    /// </summary>
    public void ReleaseCable()
    {
        if (_ownCable is not { } inlet) return;
        if (CableOwnership.FindClaim(_settings, inlet.Feed) is not { } claim) return;

        string was = claim.ClaimedName;
        bool restored = CableOwnership.Disown(_settings, claim, OutputDevices.Concat(InputDevices), out string? error);

        _settingsService.Save(_settings);
        RefreshDevices();
        RebuildDeviceRows();

        Notice = restored
            ? $"\"{was}\" is back to the name it came with. LoopIt7 will not rename it again unless you ask."
            : $"Could not put the old name back: {error}";
    }

    /// <summary>
    /// Sends the user to get a cable, and remembers that it was LoopIt7 that asked. The cable
    /// that turns up next is then ours to name, which is the difference between a stranger
    /// seeing "LoopIt7 Cable" in Discord and seeing somebody else's product name.
    /// </summary>
    public void RequestCable()
    {
        _settings.AwaitingCable = true;
        _settingsService.Save(_settings);

        try
        {
            Process.Start(new ProcessStartInfo(VirtualCableService.RecommendedCableUrl)
            {
                UseShellExecute = true
            });

            Notice = $"Install {VirtualCableService.RecommendedCableName}, then come back. LoopIt7 will pick it up and name it after your box.";
        }
        catch (Exception ex)
        {
            Notice = $"Could not open the download page: {ex.Message}. It is at {VirtualCableService.RecommendedCableUrl}.";
        }
    }

    private DestinationNodeViewModel AddDestination(AudioDeviceInfo device, NodeSettings? saved = null)
    {
        bool virtualCable = VirtualCableService.IsVirtual(device);
        string title = saved?.Title ?? device.Name;
        string subtitle = saved?.Subtitle ?? (VirtualCableService.FamilyOf(device) ?? device.InterfaceName);

        var vm = new DestinationNodeViewModel(saved?.Id ?? NewId(), title, subtitle, device.Id, virtualCable)
        {
            PickupHint = VirtualCableService.DescribePickup(device, InputDevices)
        };
        PlaceNode(vm, saved, LaneDestinationX, Destinations.Count);
        WireNode(vm);

        Destinations.Add(vm);
        _graph.AddDestination(vm.Id, title, device.Id);
        PushMix(vm);

        Finish();
        return vm;
    }

    private void PlaceNode(PatchNodeViewModel node, NodeSettings? saved, double laneX, int index)
    {
        if (saved is not null)
        {
            node.X = saved.X;
            node.Y = saved.Y;
            node.GainDb = saved.GainDb;
            node.Muted = saved.Muted;
            node.Pan = saved.Pan;
            node.Solo = saved.Solo;
            return;
        }

        node.X = laneX;
        node.Y = LaneTop + index * LaneSpacing;
    }

    private void WireNode(PatchNodeViewModel node)
    {
        node.RemoveRequested += (_, _) => RemoveNode(node);

        if (node is SourceNodeViewModel { SupportsExclusive: true } program)
        {
            program.ExclusiveChanged += (_, _) =>
            {
                ApplyExclusive(program);
                Persist();
            };
        }

        node.MixChanged += (_, _) => OnNodeMixChanged(node);
        node.SoloChanged += (_, _) => ApplySolo();
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(PatchNodeViewModel.X) or nameof(PatchNodeViewModel.Y))) return;
            UpdateCanvasExtent();
            Persist();
        };
    }

    private double _canvasWidth = 640;
    private double _canvasHeight = 400;

    /// <summary>
    /// The canvas grows to hold whatever has been dragged onto it. Without this the scroll
    /// area would stay at its starting size and boxes moved to the right would be unreachable.
    /// </summary>
    public double CanvasWidth
    {
        get => _canvasWidth;
        private set => SetProperty(ref _canvasWidth, value);
    }

    public double CanvasHeight
    {
        get => _canvasHeight;
        private set => SetProperty(ref _canvasHeight, value);
    }

    private void UpdateCanvasExtent()
    {
        double right = 640;
        double bottom = 400;

        foreach (var node in AllNodes())
        {
            right = Math.Max(right, node.X + PatchNodeViewModel.NodeWidth + 220);
            bottom = Math.Max(bottom, node.Y + PatchNodeViewModel.NodeHeight + 160);
        }

        CanvasWidth = right;
        CanvasHeight = bottom;
    }

    private void OnNodeMixChanged(PatchNodeViewModel node)
    {
        PushMix(node);
        Persist();
    }

    private void PushMix(PatchNodeViewModel node)
    {
        if (node.IsSource) _graph.ConfigureSource(node.Id, node.LinearGain, node.EffectiveMuted, node.LinearPan);
        else _graph.ConfigureDestination(node.Id, node.LinearGain, node.EffectiveMuted, node.LinearPan);
    }

    /// <summary>
    /// Solo is a property of the whole mixer, not of one channel. The moment any source is
    /// soloed every other source goes quiet, and lifting the last solo brings them all back.
    /// </summary>
    private void ApplySolo()
    {
        bool anySolo = Sources.Any(s => s.Solo);

        foreach (var source in Sources)
        {
            source.SilencedBySolo = anySolo && !source.Solo;
            PushMix(source);
        }

        OnPropertyChanged(nameof(IsSoloing));
        if (!_loading) Persist();
    }

    /// <summary>True while at least one source is soloed, so the interface can say so.</summary>
    public bool IsSoloing => Sources.Any(s => s.Solo);

    /// <summary>Clears every solo. Reachable from the transport when soloing is active.</summary>
    public void ClearSolo()
    {
        foreach (var source in Sources) source.Solo = false;
    }

    /// <summary>
    /// Hands back the cable a box was given, when the box goes. Without this, deleting a
    /// virtual output would leave a Windows device still carrying its name, pointing at
    /// something that no longer exists anywhere in the app.
    /// </summary>
    private void ReleaseInlet(PatchNodeViewModel node)
    {
        if (node is not VirtualOutputNodeViewModel box) return;
        if (box.InletFeedDeviceId.Length == 0) return;

        var feed = OutputDevices.FirstOrDefault(
            d => string.Equals(d.Id, box.InletFeedDeviceId, StringComparison.OrdinalIgnoreCase));

        // The cable is still LoopIt7's; it just no longer serves this box. So it goes back to
        // LoopIt7's own name rather than the vendor's: the way in from Windows still exists,
        // and something has to be there for the other program to pick.
        if (feed is not null && CableOwnership.FindClaim(_settings, feed) is { } claim)
        {
            CableOwnership.TryRename(
                _settings, claim, CableOwnership.DefaultNameFor(_settings),
                OutputDevices.Concat(InputDevices), out _);

            _settingsService.Save(_settings);
        }

        box.InletFeedDeviceId = string.Empty;
        box.InletHint = null;
        box.InletBadge = null;
    }

    public void RemoveNode(PatchNodeViewModel node)
    {
        foreach (var cable in Cables.Where(c => c.Source == node || c.Destination == node).ToList())
        {
            RemoveCable(cable, persist: false);
        }

        ReleaseExclusive(node.Id);
        ReleaseInlet(node);
        _graph.RemoveNode(node.Id);

        if (node is SourceNodeViewModel source) Sources.Remove(source);
        else if (node is VirtualOutputNodeViewModel virtualOutput) VirtualOutputs.Remove(virtualOutput);
        else if (node is DestinationNodeViewModel destination) Destinations.Remove(destination);

        Finish();
    }

    /// <summary>
    /// Patches a cable. Returns false when the pair is already joined, which is what the
    /// canvas needs in order to snap the dragged cable back instead of stacking a duplicate.
    /// </summary>
    public bool TryConnect(PatchNodeViewModel source, PatchNodeViewModel destination)
    {
        if (!source.CanSend || !destination.CanReceive) return false;
        if (Cables.Any(c => c.Source == source && c.Destination == destination)) return false;

        // A feedback loop builds to full scale in under a second, and it does that in
        // somebody's headphones, so it is refused rather than warned about.
        if (FeedbackGuard.WouldFeedBack(
                source, destination, Cables, Sources, InputDevices, OutputDevices, out string reason))
        {
            Notice = reason;
            return false;
        }

        string id = NewId();
        if (!_graph.Connect(id, source.Id, destination.Id)) return false;

        var cable = new CableViewModel(id, source, destination);
        cable.RemoveRequested += (_, _) => RemoveCable(cable, persist: true);
        cable.MixChanged += (_, _) =>
        {
            _graph.ConfigureConnection(cable.Id, cable.LinearGain, cable.Muted, cable.DelayMs);
            Persist();
        };

        Cables.Add(cable);
        Finish();
        return true;
    }

    public void RemoveCable(CableViewModel cable, bool persist = true)
    {
        if (SelectedCable == cable) SelectedCable = null;

        _graph.Disconnect(cable.Id);
        Cables.Remove(cable);
        cable.Dispose();

        if (persist) Finish();
    }

    /// <summary>
    /// Builds a starting patch out of whatever this machine happens to have. Everything is
    /// resolved by role at the moment the template runs, never by a stored device id, so the
    /// same two buttons make sense on a laptop with one headset and on a rig with an interface.
    /// </summary>
    public void ApplyTemplate(string kind)
    {
        var microphone = CaptureDevices.FirstOrDefault(d => d.IsSystemDefault) ?? CaptureDevices.FirstOrDefault();
        var systemAudio = LoopbackDevices.FirstOrDefault(d => d.IsSystemDefault) ?? LoopbackDevices.FirstOrDefault();
        var primaryOutput = OutputDevices.FirstOrDefault(d => d.IsSystemDefault && !VirtualCableService.IsVirtual(d))
                            ?? OutputDevices.FirstOrDefault(d => !VirtualCableService.IsVirtual(d));

        if (microphone is null || primaryOutput is null)
        {
            Notice = "This machine does not have both an input and an output to work with.";
            return;
        }

        bool wasRunning = IsRunning;
        if (wasRunning) StopRouting();

        foreach (var cable in Cables.ToList()) RemoveCable(cable, persist: false);
        foreach (var node in AllNodes().ToList()) RemoveNode(node);

        if (kind == "streaming") BuildStreamingTemplate(microphone, systemAudio, primaryOutput);
        else BuildMonitoringTemplate(microphone, primaryOutput);

        Finish();
        if (wasRunning) StartRouting();
    }

    /// <summary>
    /// Mic and everything the machine is playing, into your own ears and into a cable for the
    /// broadcast software. The cable is the part that lets the stream hear a mix that is not
    /// simply your microphone.
    /// </summary>
    private void BuildStreamingTemplate(AudioDeviceInfo microphone, AudioDeviceInfo? systemAudio, AudioDeviceInfo primaryOutput)
    {
        var mic = AddDeviceSource(microphone, loopback: false);
        var monitor = AddDestination(primaryOutput);
        TryConnect(mic, monitor);

        SourceNodeViewModel? desktop = null;
        if (systemAudio is not null)
        {
            desktop = AddDeviceSource(systemAudio, loopback: true);

            // Only monitor the desktop tap when it comes from a different device. Tapping the
            // output you are listening on and returning it there is a feedback loop, and you
            // can already hear that audio anyway.
            if (systemAudio.Id != primaryOutput.Id) TryConnect(desktop, monitor);
        }

        var cable = OutputDevices.FirstOrDefault(VirtualCableService.IsVirtual);
        if (cable is not null)
        {
            var broadcast = AddDestination(cable);
            TryConnect(mic, broadcast);
            if (desktop is not null) TryConnect(desktop, broadcast);

            Notice = broadcast.PickupHint ?? "Broadcast send ready.";
        }
        else
        {
            Notice = $"No software cable is installed, so there is nowhere to send the broadcast mix. {RecommendedCableName} is free.";
        }
    }

    /// <summary>
    /// One microphone reaching two places at once: your headphones and whatever else is in the
    /// room. The second send carries an alignment delay, because the far speaker usually needs it.
    /// </summary>
    private void BuildMonitoringTemplate(AudioDeviceInfo microphone, AudioDeviceInfo primaryOutput)
    {
        var mic = AddDeviceSource(microphone, loopback: false);
        var headphones = AddDestination(primaryOutput);
        TryConnect(mic, headphones);

        var second = OutputDevices.FirstOrDefault(d => d.Id != primaryOutput.Id && !VirtualCableService.IsVirtual(d));
        if (second is not null)
        {
            var room = AddDestination(second);
            TryConnect(mic, room);
            Notice = "Two sends from one microphone. Click the second cable to set its alignment delay.";
        }
        else
        {
            Notice = "Only one output on this machine, so there is one send. Add another output to fan out.";
        }
    }

    // Taking a program off its own output

    /// <summary>
    /// Applies or lifts the Windows mute for one program, so the patchbay is the only place
    /// it comes out. Only ever muted while routing is actually running.
    /// </summary>
    private void ApplyExclusive(SourceNodeViewModel source)
    {
        if (!source.SupportsExclusive) return;

        if (!IsRunning || !source.Exclusive)
        {
            ReleaseExclusive(source.Id);
            return;
        }

        int processId = _graph.GetApplicationProcessId(source.Id);
        if (processId <= 0) processId = source.ProcessId;
        if (processId <= 0) return;

        // A program that restarted came back under a new process id, so the note of what we
        // muted has to move with it.
        if (_silencedApps.TryGetValue(source.Id, out int previous) && previous != processId)
        {
            AppSessionControl.SetMuted(previous, false);
        }

        if (AppSessionControl.SetMuted(processId, true)) _silencedApps[source.Id] = processId;
    }

    private void ReleaseExclusive(string nodeId)
    {
        if (!_silencedApps.Remove(nodeId, out int processId)) return;
        AppSessionControl.SetMuted(processId, false);
    }

    private void ReleaseAllExclusive()
    {
        foreach (string nodeId in _silencedApps.Keys.ToList()) ReleaseExclusive(nodeId);
    }

    /// <summary>
    /// Hands back anything a previous run left muted. Every ordinary way of leaving does that
    /// already, but a killed process does not run its own shutdown, and Windows keeps a
    /// session mute across restarts. Routing is not running yet at this point, so nothing
    /// here has any business being muted by us.
    /// </summary>
    private void ReleaseLeftoverTakeovers()
    {
        foreach (var source in Sources)
        {
            if (!source.SupportsExclusive || !source.Exclusive) continue;

            int processId = _graph.GetApplicationProcessId(source.Id);
            if (processId <= 0) processId = source.ProcessId;
            if (processId > 0) AppSessionControl.SetMuted(processId, false);
        }
    }

    /// <summary>
    /// Reopens anything waiting, and reapplies the takeover mutes. Windows makes a fresh
    /// session whenever a program moves endpoint or restarts, and a fresh session is not
    /// muted, so this has to be done again rather than once.
    /// </summary>
    private void OnRetryTick(object? sender, EventArgs e)
    {
        _graph.RetryPending();

        foreach (var source in Sources)
        {
            if (source.Exclusive) ApplyExclusive(source);
        }
    }

    private void ClearPatchbay()
    {
        foreach (var cable in Cables.ToList()) RemoveCable(cable, persist: false);
        foreach (var node in AllNodes().ToList()) RemoveNode(node);

        if (IsRunning) StopRouting();
        Finish();
        Notice = "Patchbay cleared.";
    }

    private void Finish()
    {
        OnPropertyChanged(nameof(HasNodes));
        UpdateCanvasExtent();
        if (_loading) return;

        Persist();
        UpdateStatus();
    }

    // Device and application discovery

    private static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> fresh)
    {
        target.Clear();
        foreach (var item in fresh) target.Add(item);
    }

    private void RefreshDevices()
    {
        ReadEndpoints();

        bool changed = CableOwnership.ObserveCables(_settings, OutputDevices.Concat(InputDevices));

        if (NameOwnCable())
        {
            // The endpoints answer to a different name now, and every list in the app is
            // still holding the one they had a second ago. Read them again rather than
            // showing the old names until the next time Windows says something changed.
            ReadEndpoints();
            changed = true;
        }

        // Saved even mid load: a rename that is not written down is a device left carrying
        // LoopIt7's name with nothing on disk saying how to put it back.
        if (changed) _settingsService.Save(_settings);

        UpdateCableStatus();
    }

    private void ReadEndpoints()
    {
        var sources = _devices.GetInputSources();
        SyncDevices(InputDevices, sources);
        SyncDevices(CaptureDevices, [.. sources.Where(d => d.Kind == AudioSourceKind.Capture)]);
        SyncDevices(LoopbackDevices, [.. sources.Where(d => d.Kind == AudioSourceKind.Loopback)]);
        SyncDevices(OutputDevices, _devices.GetOutputs());
        Sync(CableInlets, VirtualCableService.FindInlets(OutputDevices, InputDevices));
        OnPropertyChanged(nameof(HasVirtualCable));
    }

    /// <summary>
    /// Puts LoopIt7's name on a cable of its own the moment one turns up, without waiting to
    /// be asked.
    /// <para>
    /// The cable is the only way anything else in Windows can reach LoopIt7: a DAW, Discord
    /// and OBS all pick it by name from their own output list. A cable sitting there called
    /// "CABLE Input" is a way in nobody was told to look for, so the name goes on as soon as
    /// the cable exists rather than when the user first wires something up.
    /// </para>
    /// <para>
    /// Only a cable LoopIt7 installed, or one fetched at its asking, is touched.
    /// <see cref="CableOwnership"/> holds that line, and the names being replaced are written
    /// down so the uninstaller can put them back.
    /// </para>
    /// </summary>
    /// <returns>True when something was renamed, so the caller knows to look again.</returns>
    private bool NameOwnCable()
    {
        bool renamed = false;

        foreach (var inlet in CableInlets.ToList())
        {
            if (!CableOwnership.MayNameUnasked(_settings, inlet.Feed)) continue;
            if (!CableOwnership.MayNameUnasked(_settings, inlet.Pickup)) continue;
            if (CableOwnership.IsOwned(_settings, inlet.Feed)) continue;

            if (CableOwnership.TryClaim(
                    _settings, inlet.Feed, inlet.Pickup, CableOwnership.DefaultNameFor(_settings), out _))
            {
                renamed = true;
            }
        }

        return renamed;
    }

    /// <summary>
    /// Works out what the Devices page has to say about naming: which cable carries LoopIt7's
    /// name, and which one could be asked to.
    /// </summary>
    private void UpdateCableStatus()
    {
        var inlets = CableInlets.ToList();

        _ownCable = inlets.FirstOrDefault(i => CableOwnership.IsOwned(_settings, i.Feed));

        // Only a cable that is a cable and nothing else. Wave Link's mixes and VoiceMeeter's
        // VAIO are virtual devices too, and each one is part of a program that finds it by the
        // name it has: renaming one of those would break the program it belongs to, and the
        // user asking for it would not have meant that.
        _adoptableCable = inlets
            .Where(i => !CableOwnership.IsOwned(_settings, i.Feed))
            .Where(i => VirtualCableService.IsPlainCable(i.Feed))
            .OrderByDescending(i => VirtualCableService.FamilyOf(i.Feed) == "VB-Audio Cable")
            .FirstOrDefault();

        OnPropertyChanged(nameof(OwnCableName));
        OnPropertyChanged(nameof(HasOwnCable));
        OnPropertyChanged(nameof(AdoptableCableName));
        OnPropertyChanged(nameof(CanAdoptCable));
    }

    private static void SyncDevices(ObservableCollection<AudioDeviceInfo> target, IReadOnlyList<AudioDeviceInfo> fresh)
    {
        target.Clear();
        foreach (var device in fresh) target.Add(device);
    }

    private void RefreshApplications()
    {
        var fresh = ApplicationService.GetApplications();
        Applications.Clear();
        foreach (var application in fresh) Applications.Add(application);
    }

    private void RebuildDeviceRows()
    {
        DeviceRows.Clear();

        // Outputs first: that is the half people come here to change.
        foreach (var device in OutputDevices)
        {
            DeviceRows.Add(new DeviceRowViewModel(device, _devices, OnDeviceRowChanged, InputDevices));
        }

        foreach (var device in InputDevices.Where(d => d.Kind == AudioSourceKind.Capture))
        {
            DeviceRows.Add(new DeviceRowViewModel(device, _devices, OnDeviceRowChanged, InputDevices));
        }
    }

    private void OnDeviceRowChanged() => _dispatcher.BeginInvoke(() =>
    {
        RefreshDevices();

        foreach (var row in DeviceRows)
        {
            var fresh = OutputDevices.Concat(InputDevices).FirstOrDefault(d => d.Id == row.Id && d.Kind != AudioSourceKind.Loopback);
            if (fresh is not null) row.Device = fresh;
        }
    });

    private void OnDevicesChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        _deviceTimer.Stop();
        _deviceTimer.Start();
    });

    private void OnDeviceSettleTick(object? sender, EventArgs e)
    {
        _deviceTimer.Stop();

        RefreshDevices();
        RebuildDeviceRows();
        Midi.Refresh();

        if (IsRunning) _graph.RetryPending();
        UpdateStatus();
    }

    private void OnNodeFailed(object? sender, GraphErrorEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        var node = AllNodes().FirstOrDefault(n => n.Id == e.NodeId);
        Notice = node is not null ? $"{node.Title}: {e.Message}" : e.Message;
        _graph.RetryPending();
    });

    // Metering

    private void OnMeterTick(object? sender, EventArgs e)
    {
        int live = 0;

        foreach (var source in Sources)
        {
            var status = _graph.GetSourceStatus(source.Id, out string? detail, out int rate, out int channels);
            source.ApplyStatus(status, detail, rate, channels);
            source.Peak = status == NodeStatus.Live ? _graph.ReadSourcePeak(source.Id) : 0;
            if (status == NodeStatus.Live) live++;

            if (source.Kind == SourceKind.Application)
            {
                int processId = _graph.GetApplicationProcessId(source.Id);
                if (processId > 0) source.ProcessId = processId;
            }
        }

        foreach (var virtualOutput in VirtualOutputs)
        {
            // The engine drives a virtual output as a source, so it reports like one.
            var busStatus = _graph.GetSourceStatus(virtualOutput.Id, out string? busDetail, out int busRate, out int busChannels);
            virtualOutput.ApplyStatus(busStatus, busDetail, busRate, busChannels);
            virtualOutput.Peak = busStatus == NodeStatus.Live ? _graph.ReadSourcePeak(virtualOutput.Id) : 0;
            if (busStatus == NodeStatus.Live) live++;
        }

        foreach (var destination in Destinations)
        {
            var status = _graph.GetDestinationStatus(destination.Id, out string? detail, out int rate, out int channels);
            destination.ApplyStatus(status, detail, rate, channels);
            destination.Peak = status == NodeStatus.Live ? _graph.ReadDestinationPeak(destination.Id) : 0;
            if (status == NodeStatus.Live) live++;
        }

        foreach (var cable in Cables)
        {
            cable.Peak = _graph.ReadConnectionPeak(cable.Id);
        }

        Midi.Tick();

        if (live != _liveNodeCount)
        {
            _liveNodeCount = live;
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        if (!IsRunning)
        {
            StatusText = Cables.Count == 0
                ? "Nothing patched yet"
                : $"Stopped · {Describe(Cables.Count, "cable")}";
            return;
        }

        int liveSources = Sources.Count(s => s.IsLive);
        int liveDestinations = Destinations.Count(d => d.IsLive);

        string buses = VirtualOutputs.Count > 0
            ? $"{Describe(VirtualOutputs.Count, "virtual output")} · "
            : string.Empty;

        StatusText = $"Live · {Describe(liveSources, "source")} · {Describe(Cables.Count, "cable")} · " +
                     $"{buses}{Describe(liveDestinations, "output")} · {LatencyMs} ms buffer";
    }

    private static string Describe(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // Presets and persistence

    private void SavePreset()
    {
        var preset = new PatchPreset
        {
            Name = NewPresetName.Trim(),
            LatencyMs = LatencyMs,
            Nodes = CaptureNodes(),
            Cables = CaptureCables()
        };

        var existing = Presets.FirstOrDefault(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) Presets[Presets.IndexOf(existing)] = preset;
        else Presets.Add(preset);

        NewPresetName = string.Empty;
        Notice = $"Saved \"{preset.Name}\".";
        Persist();
    }

    private void LoadPreset(PatchPreset? preset)
    {
        if (preset is null) return;

        bool wasRunning = IsRunning;
        if (wasRunning) StopRouting();

        foreach (var cable in Cables.ToList()) RemoveCable(cable, persist: false);
        foreach (var node in AllNodes().ToList()) RemoveNode(node);

        LatencyMs = preset.LatencyMs;
        RestoreWorkspace(preset.Nodes, preset.Cables);

        Notice = $"Loaded \"{preset.Name}\".";
        Finish();

        if (wasRunning) StartRouting();
    }

    private void DeletePreset(PatchPreset? preset)
    {
        if (preset is null) return;
        Presets.Remove(preset);
        Persist();
    }

    /// <summary>
    /// Rebuilds the patchbay from saved state. Nodes whose device or program is missing are
    /// still created: they show as waiting and reconnect on their own when it returns.
    /// </summary>
    private void RestoreWorkspace(List<NodeSettings> nodes, List<CableSettings> cables)
    {
        bool wasLoading = _loading;
        _loading = true;

        var byId = new Dictionary<string, PatchNodeViewModel>();

        foreach (var saved in nodes)
        {
            PatchNodeViewModel? created = saved.Kind switch
            {
                "Destination" => RestoreDestination(saved),
                "VirtualOutput" => AddVirtualOutput(saved.Title, saved),
                "Application" => RestoreApplication(saved),
                _ => RestoreDeviceSource(saved)
            };

            if (created is not null) byId[saved.Id] = created;
        }

        foreach (var cable in cables)
        {
            if (!byId.TryGetValue(cable.SourceId, out var source) || !source.CanSend) continue;
            if (!byId.TryGetValue(cable.DestinationId, out var destination) || !destination.CanReceive) continue;

            if (!TryConnect(source, destination)) continue;

            var created = Cables[^1];
            created.GainDb = cable.GainDb;
            created.Muted = cable.Muted;
            created.DelayMs = cable.DelayMs;
        }

        RestoreInletHints();

        _loading = wasLoading;
        OnPropertyChanged(nameof(HasNodes));
    }

    /// <summary>
    /// Puts the "way in from Windows" label back on the cards that had one. The patch itself is
    /// saved, so this is only the wording, and it is rebuilt from the endpoint the user actually
    /// chose rather than worked out again, which on a driver with several recording ends would
    /// be a different answer from the one they picked.
    /// </summary>
    private void RestoreInletHints()
    {
        foreach (var box in VirtualOutputs)
        {
            if (box.InletFeedDeviceId.Length == 0) continue;

            var feed = OutputDevices.FirstOrDefault(
                d => string.Equals(d.Id, box.InletFeedDeviceId, StringComparison.OrdinalIgnoreCase));

            if (feed is null)
            {
                // The cable is gone: unplugged, uninstalled, or a driver update renamed it.
                // Saying nothing is better than pointing at a device that is not there.
                box.InletFeedDeviceId = string.Empty;
                box.InletHint = null;
                box.InletBadge = null;
                continue;
            }

            var pickup = Cables
                .Where(c => c.Destination == box)
                .Select(c => c.Source)
                .OfType<SourceNodeViewModel>()
                .Where(src => src.Kind == SourceKind.Device)
                .Select(src => InputDevices.FirstOrDefault(
                    d => d.Id == src.DeviceId && d.Kind == AudioSourceKind.Capture))
                .FirstOrDefault(d => d is not null && VirtualCableService.IsVirtual(d));

            box.InletHint = pickup is null
                ? $"Choose \"{feed.Name}\" as the output in any program that should play through {box.Title}."
                : $"Choose \"{feed.Name}\" as the output in any program that should play through {box.Title}. LoopIt7 listens on \"{pickup.Name}\".";

            box.InletBadge = $"via {feed.Name}";
        }
    }

    private PatchNodeViewModel RestoreDeviceSource(NodeSettings saved)
    {
        bool loopback = saved.Kind == "DeviceLoopback";
        var kind = loopback ? AudioSourceKind.Loopback : AudioSourceKind.Capture;

        var device = InputDevices.FirstOrDefault(d => d.Id == saved.DeviceId && d.Kind == kind)
                     ?? new AudioDeviceInfo(saved.DeviceId, saved.Title, saved.Subtitle, kind, false);

        return AddDeviceSource(device, loopback, saved);
    }

    private PatchNodeViewModel RestoreApplication(NodeSettings saved)
    {
        var running = Applications.FirstOrDefault(a =>
            string.Equals(a.ExecutableName, saved.ExecutableName, StringComparison.OrdinalIgnoreCase));

        var application = running ?? new AudioApplication(0, saved.ExecutableName, saved.Title, false);
        var restored = AddApplicationSource(application, saved);
        restored.Exclusive = saved.Exclusive;
        return restored;
    }

    private PatchNodeViewModel RestoreDestination(NodeSettings saved)
    {
        var device = OutputDevices.FirstOrDefault(d => d.Id == saved.DeviceId)
                     ?? new AudioDeviceInfo(saved.DeviceId, saved.Title, saved.Subtitle, AudioSourceKind.Render, false);

        return AddDestination(device, saved);
    }

    private List<NodeSettings> CaptureNodes()
    {
        var list = new List<NodeSettings>();

        foreach (var source in Sources)
        {
            list.Add(new NodeSettings
            {
                Id = source.Id,
                Kind = source.Kind switch
                {
                    SourceKind.DeviceLoopback => "DeviceLoopback",
                    SourceKind.Application => "Application",
                    _ => "Device"
                },
                Title = source.Title,
                Subtitle = source.Subtitle,
                DeviceId = source.DeviceId,
                ExecutableName = source.ExecutableName,
                X = source.X,
                Y = source.Y,
                GainDb = source.GainDb,
                Muted = source.Muted,
                Pan = source.Pan,
                Solo = source.Solo,
                Exclusive = source.Exclusive
            });
        }

        foreach (var virtualOutput in VirtualOutputs)
        {
            list.Add(new NodeSettings
            {
                Id = virtualOutput.Id,
                Kind = "VirtualOutput",
                Title = virtualOutput.Title,
                Subtitle = virtualOutput.Subtitle,
                InletFeedDeviceId = virtualOutput.InletFeedDeviceId,
                X = virtualOutput.X,
                Y = virtualOutput.Y,
                GainDb = virtualOutput.GainDb,
                Muted = virtualOutput.Muted,
                Pan = virtualOutput.Pan
            });
        }

        foreach (var destination in Destinations)
        {
            list.Add(new NodeSettings
            {
                Id = destination.Id,
                Kind = "Destination",
                Title = destination.Title,
                Subtitle = destination.Subtitle,
                DeviceId = destination.DeviceId,
                X = destination.X,
                Y = destination.Y,
                GainDb = destination.GainDb,
                Muted = destination.Muted,
                Pan = destination.Pan
            });
        }

        return list;
    }

    private List<CableSettings> CaptureCables() =>
    [
        .. Cables.Select(c => new CableSettings
        {
            Id = c.Id,
            SourceId = c.Source.Id,
            DestinationId = c.Destination.Id,
            GainDb = c.GainDb,
            Muted = c.Muted,
            DelayMs = c.DelayMs
        })
    ];

    private void Persist()
    {
        if (_loading || _disposed) return;

        _settings.LatencyMs = LatencyMs;
        _settings.Nodes = CaptureNodes();
        _settings.Cables = CaptureCables();
        _settings.Presets = [.. Presets];
        _settings.MidiRoutes = [.. Midi.NamedRoutes().Select(r => new MidiRouteSettings { Input = r.Input, Output = r.Output })];

        _settingsService.Save(_settings);
    }

    public void SaveNow() => Persist();

    private void OpenSettingsFolder() =>
        OpenUrl(System.IO.Path.GetDirectoryName(_settingsService.SettingsPath) ?? string.Empty);

    private void OpenUrl(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch
        {
            Notice = "Windows would not open that.";
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (_disposed) return;

        Persist();
        _disposed = true;

        // Last chance to hand every program back its own output.
        ReleaseAllExclusive();

        _meterTimer.Stop();
        _deviceTimer.Stop();
        _retryTimer.Stop();
        _noticeTimer.Stop();

        _devices.DevicesChanged -= OnDevicesChanged;
        _graph.NodeFailed -= OnNodeFailed;

        Midi.Dispose();
        _graph.Dispose();
        _devices.Dispose();
    }
}
