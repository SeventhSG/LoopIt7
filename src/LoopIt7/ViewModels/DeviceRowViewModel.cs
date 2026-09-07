using System.Collections.ObjectModel;
using LoopIt7.Audio;

namespace LoopIt7.ViewModels;

/// <summary>
/// One endpoint in the device panel, with the two things Windows buries three dialogs deep:
/// which device is default, and what format the shared engine runs it at.
/// </summary>
public sealed class DeviceRowViewModel : ObservableObject
{
    private readonly DeviceService _devices;
    private readonly Action _refreshRequested;

    private AudioDeviceInfo _device;
    private bool _isExpanded;
    private bool _ratesLoaded;
    private int _selectedSampleRate;
    private int _selectedBitDepth = 24;
    private string? _message;

    public DeviceRowViewModel(
        AudioDeviceInfo device,
        DeviceService devices,
        Action refreshRequested,
        IEnumerable<AudioDeviceInfo> captureDevices)
    {
        _device = device;
        _devices = devices;
        _refreshRequested = refreshRequested;
        PickupHint = VirtualCableService.DescribePickup(device, captureDevices);

        _selectedSampleRate = device.SampleRate > 0 ? device.SampleRate : 48000;
        if (device.BitDepth is 16 or 24 or 32) _selectedBitDepth = device.BitDepth;

        MakeDefaultCommand = new RelayCommand(_ => MakeDefault(), _ => !_device.IsSystemDefault);
        ApplyFormatCommand = new RelayCommand(_ => ApplyFormat());
        ResetFormatCommand = new RelayCommand(_ => ResetFormat());
        ToggleExpandCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
        OpenWindowsSettingsCommand = new RelayCommand(_ => EndpointControl.OpenWindowsSoundSettings());
    }

    public string Id => _device.Id;

    public AudioDeviceInfo Device
    {
        get => _device;
        set
        {
            _device = value;
            OnPropertyChanged(nameof(Device));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(InterfaceName));
            OnPropertyChanged(nameof(FormatText));
            OnPropertyChanged(nameof(IsDefault));
            OnPropertyChanged(nameof(DirectionText));
        }
    }

    public string Name => _device.Name;
    public string InterfaceName => _device.InterfaceName;
    public string FormatText => _device.FormatText;
    public bool IsDefault => _device.IsSystemDefault;
    public bool IsInput => _device.IsInput;

    public string DirectionText => _device.IsInput ? "Input" : "Output";

    public string? VirtualFamily => VirtualCableService.FamilyOf(_device);

    public bool IsVirtual => VirtualFamily is not null;

    /// <summary>What to select in the other program once audio is sent to this cable.</summary>
    public string? PickupHint { get; }

    public bool HasPickupHint => !string.IsNullOrEmpty(PickupHint);

    public ObservableCollection<int> SampleRates { get; } = [];

    public IReadOnlyList<int> BitDepths => EndpointControl.StandardBitDepths;

    public RelayCommand MakeDefaultCommand { get; }
    public RelayCommand ApplyFormatCommand { get; }
    public RelayCommand ResetFormatCommand { get; }
    public RelayCommand ToggleExpandCommand { get; }
    public RelayCommand OpenWindowsSettingsCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (value) LoadSupportedRates();
        }
    }

    public int SelectedSampleRate
    {
        get => _selectedSampleRate;
        set => SetProperty(ref _selectedSampleRate, value);
    }

    public int SelectedBitDepth
    {
        get => _selectedBitDepth;
        set
        {
            if (!SetProperty(ref _selectedBitDepth, value)) return;

            // Which rates the hardware accepts depends on the bit depth, so ask again.
            _ratesLoaded = false;
            LoadSupportedRates();
        }
    }

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    /// <summary>
    /// Asks the hardware in exclusive mode which rates it can really run. Shared mode would
    /// say yes to everything, because the engine would just convert behind our backs.
    /// </summary>
    private void LoadSupportedRates()
    {
        if (_ratesLoaded) return;
        _ratesLoaded = true;

        SampleRates.Clear();

        using var device = _devices.TryGetDevice(_device.Id);
        if (device is null)
        {
            foreach (int rate in EndpointControl.StandardSampleRates) SampleRates.Add(rate);
            return;
        }

        int channels = _device.Channels > 0 ? _device.Channels : 2;
        var supported = EndpointControl.GetSupportedSampleRates(device, _selectedBitDepth, channels);

        foreach (int rate in supported.Count > 0 ? supported : EndpointControl.StandardSampleRates)
        {
            SampleRates.Add(rate);
        }

        if (!SampleRates.Contains(_selectedSampleRate) && SampleRates.Count > 0)
        {
            SelectedSampleRate = SampleRates.Contains(48000) ? 48000 : SampleRates[0];
        }
    }

    private void MakeDefault()
    {
        Message = EndpointControl.TrySetDefault(_device.Id, out string? error)
            ? "Now the default device."
            : $"Windows refused: {error}";

        _refreshRequested();
    }

    private void ApplyFormat()
    {
        int channels = _device.Channels > 0 ? _device.Channels : 2;

        if (EndpointControl.TrySetSharedFormat(_device.Id, _selectedSampleRate, _selectedBitDepth, channels, out string? error))
        {
            Message = "Format applied. Programs already playing keep the old one until they reopen the device.";
            _refreshRequested();
        }
        else
        {
            Message = $"Windows refused: {error}";
        }
    }

    private void ResetFormat()
    {
        Message = EndpointControl.TryResetFormat(_device.Id, out string? error)
            ? "Back to the driver's own default."
            : $"Windows refused: {error}";

        _refreshRequested();
    }
}
