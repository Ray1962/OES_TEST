using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Aqst.OesSpectrometer;
using Aqst.OesSpectrometer.Models;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

#nullable enable

namespace OesTest;

public sealed class DeviceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly LineSeries _series;
    private readonly LinearAxis _intensityAxis;
    private OesSpectrometer? _device;
    private SpectrumSample? _lastSample;

    public DeviceViewModel(string name, OxyColor seriesColor)
    {
        Name = name;
        PlotModel = new PlotModel
        {
            Title = $"{name} Spectrum",
            TitleFontSize = 14,
            Background = OxyColors.White,
            PlotAreaBorderColor = OxyColor.FromRgb(0xCC, 0xCC, 0xCC),
        };
        PlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Title = "Wavelength (nm)",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE5, 0xE5, 0xE5),
            MinorGridlineStyle = LineStyle.Dot,
            MinorGridlineColor = OxyColor.FromRgb(0xF0, 0xF0, 0xF0),
        });
        _intensityAxis = new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Intensity (a.u.)",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE5, 0xE5, 0xE5),
            MinorGridlineStyle = LineStyle.Dot,
            MinorGridlineColor = OxyColor.FromRgb(0xF0, 0xF0, 0xF0),
        };
        PlotModel.Axes.Add(_intensityAxis);
        _series = new LineSeries
        {
            Color = seriesColor,
            StrokeThickness = 1.0,
            LineStyle = LineStyle.Solid,
            CanTrackerInterpolatePoints = false,
        };
        PlotModel.Series.Add(_series);

        ConnectCommand     = new RelayCommand(async () => await ConnectAsync(),     () => !IsConnected && !IsBusy);
        DisconnectCommand  = new RelayCommand(async () => await DisconnectAsync(),  () =>  IsConnected && !IsBusy);
        StartCommand       = new RelayCommand(StartAcquisition,                     () =>  IsConnected && !IsAcquiring);
        StopCommand        = new RelayCommand(StopAcquisition,                      () =>  IsAcquiring);
        ApplyParamsCommand = new RelayCommand(async () => await ApplyParametersAsync(), () => IsConnected && !IsBusy);
        SaveCsvCommand     = new RelayCommand(SaveSpectrumToCsv,                    () => _lastSample is not null);
    }

    public string Name { get; }
    public PlotModel PlotModel { get; }

    private float _integrationTimeMs = 50f;
    public float IntegrationTimeMs { get => _integrationTimeMs; set => Set(ref _integrationTimeMs, value); }

    private uint _averageCount = 1;
    public uint AverageCount { get => _averageCount; set => Set(ref _averageCount, value); }

    private int _pollingIntervalMs = 200;
    public int PollingIntervalMs { get => _pollingIntervalMs; set => Set(ref _pollingIntervalMs, value); }

    private bool _forceTestMode = false;
    public bool ForceTestMode { get => _forceTestMode; set => Set(ref _forceTestMode, value); }

    // Off by default (matches the SDK 0.4.3 default). When on, the app probes the device for
    // background-remove support right after connecting — see ProbeBackgroundRemoveIfEnabledAsync.
    private bool _enableBackgroundRemove = false;
    public bool EnableBackgroundRemove { get => _enableBackgroundRemove; set => Set(ref _enableBackgroundRemove, value); }

    private OesConnectionType _connectionType = OesConnectionType.Usb;
    /// <summary>USB (enumerate + open index 0) vs Ethernet (open <see cref="IpAddress"/> directly).</summary>
    public OesConnectionType ConnectionType
    {
        get => _connectionType;
        set { if (Set(ref _connectionType, value)) OnPropertyChanged(nameof(IsEthernetSelected)); }
    }

    /// <summary>Enum values backing the connection-type selector's ItemsSource.</summary>
    public OesConnectionType[] ConnectionTypes { get; } =
        (OesConnectionType[])Enum.GetValues(typeof(OesConnectionType));

    /// <summary>True when <see cref="ConnectionType"/> is Ethernet — drives the IP textbox visibility.</summary>
    public bool IsEthernetSelected => ConnectionType == OesConnectionType.Ethernet;

    private string _ipAddress = string.Empty;
    /// <summary>Target IPv4 address used when <see cref="ConnectionType"/> is Ethernet.</summary>
    public string IpAddress { get => _ipAddress; set => Set(ref _ipAddress, value); }

    private OesAcquireMode _acquireMode = OesAcquireMode.HWAvg;
    /// <summary>Native acquisition method. Hot-applied on a live device via the Apply button.</summary>
    public OesAcquireMode AcquireMode { get => _acquireMode; set => Set(ref _acquireMode, value); }

    /// <summary>Enum values backing the acquire-mode selector's ItemsSource.</summary>
    public OesAcquireMode[] AcquireModes { get; } =
        (OesAcquireMode[])Enum.GetValues(typeof(OesAcquireMode));

    private OesAverageMode _averageMode = OesAverageMode.Hardware;
    /// <summary>Hardware vs software frame averaging. Hot-applied on a live device via the Apply button.</summary>
    public OesAverageMode AverageMode { get => _averageMode; set => Set(ref _averageMode, value); }

    /// <summary>Enum values backing the average-mode selector's ItemsSource.</summary>
    public OesAverageMode[] AverageModes { get; } =
        (OesAverageMode[])Enum.GetValues(typeof(OesAverageMode));

    /// <summary>
    /// Connect-time parameters (connection type, IP, background remove, force-test-mode) are baked into
    /// <see cref="OesParameters"/> when the device wrapper is built. Changing them on a live device does
    /// nothing until reconnect, so their controls are only editable while disconnected — set before Connect.
    /// </summary>
    public bool IsPreConnectEditable => !IsConnected && !IsBusy;

    private DeviceConnectionStatus _status = DeviceConnectionStatus.Disconnected;
    public DeviceConnectionStatus Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                RaiseCanExec();
            }
        }
    }
    public string StatusText => Status.ToString();

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (Set(ref _isConnected, value)) { RaiseCanExec(); OnPropertyChanged(nameof(IsPreConnectEditable)); } }
    }

    private bool _isAcquiring;
    public bool IsAcquiring
    {
        get => _isAcquiring;
        private set { if (Set(ref _isAcquiring, value)) RaiseCanExec(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) { RaiseCanExec(); OnPropertyChanged(nameof(IsPreConnectEditable)); } }
    }

    private string _serialNumber = "—";
    public string SerialNumber { get => _serialNumber; private set => Set(ref _serialNumber, value); }

    private int _frameSize;
    public int FrameSize { get => _frameSize; private set => Set(ref _frameSize, value); }

    private DateTime? _lastFrameTime;
    public DateTime? LastFrameTime
    {
        get => _lastFrameTime;
        private set
        {
            if (Set(ref _lastFrameTime, value))
                OnPropertyChanged(nameof(LastFrameTimeText));
        }
    }
    public string LastFrameTimeText => LastFrameTime?.ToString("HH:mm:ss.fff") ?? "—";

    private bool _isTestMode;
    public bool IsTestMode { get => _isTestMode; private set => Set(ref _isTestMode, value); }

    private string _statusMessage = "Ready";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    public RelayCommand ConnectCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ApplyParamsCommand { get; }
    public RelayCommand SaveCsvCommand { get; }

    private Task ConnectAsync() => ConnectStandaloneAsync();

    /// <summary>
    /// Same as the per-device Connect button: each instance independently enumerates and opens
    /// device index 0. Use this only for standalone / test-mode slots — never for two real
    /// hardware devices in the same process. For dual hardware, use <see cref="AttachAsync"/>
    /// with a handle from <see cref="OesDiscovery.OpenAllDevices"/>.
    /// </summary>
    public async Task ConnectStandaloneAsync()
    {
        IsBusy = true;
        StatusMessage = "Connecting…";
        try
        {
            CreateDeviceWrapper();
            bool ok = await _device!.ConnectAsync();
            ApplyConnectResult(ok);
            if (ok) await ProbeBackgroundRemoveIfEnabledAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Error: " + ex.Message;
            MessageBox.Show(ex.ToString(), $"{Name} connect failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Adopt a native handle that <see cref="OesDiscovery"/> already opened. Used by the
    /// "Connect Both" flow to avoid two independent <c>UAI_SpectrometerOpen(0, ...)</c>
    /// calls colliding on the same physical device.
    /// </summary>
    public async Task AttachAsync(OpenedHandle handle)
    {
        IsBusy = true;
        StatusMessage = "Attaching…";
        try
        {
            CreateDeviceWrapper();
            bool ok = await _device!.AttachAsync(handle);
            ApplyConnectResult(ok);
            if (ok) await ProbeBackgroundRemoveIfEnabledAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Error: " + ex.Message;
            MessageBox.Show(ex.ToString(), $"{Name} attach failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CreateDeviceWrapper()
    {
        DetachDevice();
        _device = new OesSpectrometer(BuildParameters());
        _device.StatusChanged     += OnStatusChanged;
        _device.ErrorOccurred     += OnErrorOccurred;
        _device.DllNotFound       += OnDllNotFound;
        _device.SpectrumAvailable += OnSpectrumAvailable;
        _device.ForceSimulation(ForceTestMode);
    }

    private void ApplyConnectResult(bool ok)
    {
        if (ok && _device is not null)
        {
            IsConnected = true;
            SerialNumber = string.IsNullOrWhiteSpace(_device.DeviceInfo.SerialNumber)
                ? "—" : _device.DeviceInfo.SerialNumber;
            FrameSize = _device.DeviceInfo.FrameSize;
            IsTestMode = _device.DeviceInfo.IsSimulated;
            StatusMessage = $"Connected{(IsTestMode ? " (Test Mode)" : "")}";
        }
        else
        {
            StatusMessage = "Connect failed: " + (_device?.LastConnectionAttemptResult ?? "no device");
        }
    }

    /// <summary>
    /// When Background Remove is enabled, probe the freshly-connected device once (SDK runs
    /// UAI_BackgroundRemove against a single frame). If the unit rejects it, warn the user and clear
    /// the option so acquisition streams raw intensities. Skipped in test mode — there is no real
    /// hardware correction to exercise there.
    /// </summary>
    private async Task ProbeBackgroundRemoveIfEnabledAsync()
    {
        if (_device is null || !IsConnected || !EnableBackgroundRemove || IsTestMode) return;

        var support = await _device.ProbeBackgroundRemoveAsync();
        if (support == BackgroundRemoveSupport.Unsupported)
        {
            EnableBackgroundRemove = false; // reflect reality: the correction is off from here on
            StatusMessage = "Background Remove 不支援，已停用";
            MessageBox.Show(
                $"此光譜儀（序號 {SerialNumber}）不支援 Background Remove 背景校正。\n\n" +
                "已自動停用該選項，將以原始強度 (raw intensities) 進行擷取。",
                $"{Name} — Background Remove 不支援",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task DisconnectAsync()
    {
        if (_device is null) return;
        IsBusy = true;
        try
        {
            if (IsAcquiring) StopAcquisition();
            await _device.DisconnectAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = "Disconnect error: " + ex.Message;
        }
        finally
        {
            IsConnected = false;
            IsAcquiring = false;
            IsTestMode = false;
            StatusMessage = "Disconnected";
            IsBusy = false;
        }
    }

    private void StartAcquisition()
    {
        if (_device is null) return;
        if (_device.StartAcquisition())
        {
            IsAcquiring = true;
            StatusMessage = "Acquiring…";
        }
    }

    private void StopAcquisition()
    {
        if (_device is null) return;
        _device.StopAcquisition();
        IsAcquiring = false;
        StatusMessage = "Stopped";
    }

    private async Task ApplyParametersAsync()
    {
        if (_device is null) return;
        IsBusy = true;
        try
        {
            await _device.UpdateParametersAsync(BuildParameters());
            StatusMessage = "Parameters updated";
        }
        catch (Exception ex)
        {
            StatusMessage = "Apply failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private OesParameters BuildParameters() => new()
    {
        IntegrationTimeMs      = IntegrationTimeMs,
        AverageCount           = AverageCount,
        PollingIntervalMs      = PollingIntervalMs,
        ForceSimulation        = ForceTestMode,
        EnableBackgroundRemove = EnableBackgroundRemove,
        ConnectionType         = ConnectionType,
        IpAddress              = IpAddress,
        AcquireMode            = AcquireMode,
        AverageMode            = AverageMode,
    };

    private void OnStatusChanged(object? sender, DeviceConnectionStatus s) =>
        _dispatcher.BeginInvoke(() =>
        {
            Status = s;
            // The SDK stops the acquisition loop by itself after MaxConsecutiveErrors and reports it
            // by leaving the Acquiring state. Follow it, or the panel keeps claiming it is acquiring
            // and Start stays disabled with nothing running.
            if (s != DeviceConnectionStatus.Acquiring && IsAcquiring)
            {
                IsAcquiring = false;
                StatusMessage = "Acquisition stopped by device";
            }
        });

    private void OnErrorOccurred(object? sender, OesErrorEventArgs e) =>
        _dispatcher.BeginInvoke(() => StatusMessage = "Error: " + e.Message);

    private void OnDllNotFound(object? sender, DllNotFoundNotification n) =>
        _dispatcher.BeginInvoke(() =>
            StatusMessage = "UserApplication.dll not found — running in Test Mode.");

    private void OnSpectrumAvailable(object? sender, SpectrumSample sample) =>
        _dispatcher.BeginInvoke(() => UpdatePlot(sample));

    private void UpdatePlot(SpectrumSample sample)
    {
        var wl = sample.Wavelengths;
        var inten = sample.Intensities;
        int n = Math.Min(wl.Length, inten.Length);

        var points = _series.Points;
        points.Clear();
        if (points.Capacity < n) points.Capacity = n;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            double y = inten[i];
            points.Add(new DataPoint(wl[i], y));
            if (y < min) min = y;
            if (y > max) max = y;
        }

        // Follow the Y axis to this frame's intensity range so the trace fills the plot and changes
        // stay visible. A 5% margin above/below keeps peaks off the frame edge; a flat frame gets a
        // small fallback span so the axis doesn't collapse to zero height.
        if (n > 0 && !double.IsInfinity(min) && !double.IsInfinity(max))
        {
            double pad = max > min ? (max - min) * 0.05 : Math.Max(1.0, Math.Abs(max) * 0.05);
            _intensityAxis.Minimum = min - pad;
            _intensityAxis.Maximum = max + pad;
        }

        LastFrameTime = sample.Timestamp;
        IsTestMode = sample.IsSimulated;
        if (FrameSize == 0) FrameSize = n;
        if (SerialNumber == "—" && !string.IsNullOrWhiteSpace(sample.SerialNumber))
            SerialNumber = sample.SerialNumber;

        bool hadSample = _lastSample is not null;
        _lastSample = sample;
        if (!hadSample) SaveCsvCommand.RaiseCanExecuteChanged();

        PlotModel.InvalidatePlot(true);
    }

    private void SaveSpectrumToCsv()
    {
        var sample = _lastSample;
        if (sample is null)
        {
            StatusMessage = "No spectrum available yet.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = $"Save {Name} Spectrum",
            Filter = "CSV file (*.csv)|*.csv|All files (*.*)|*.*",
            DefaultExt = ".csv",
            FileName = BuildDefaultCsvName(sample),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            WriteCsv(dlg.FileName, sample);
            StatusMessage = "Saved: " + dlg.FileName;
        }
        catch (Exception ex)
        {
            StatusMessage = "Save failed: " + ex.Message;
            MessageBox.Show(ex.ToString(), $"{Name} save CSV failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string BuildDefaultCsvName(SpectrumSample sample)
    {
        string safeName = string.Concat(Name.Split(Path.GetInvalidFileNameChars()));
        string serial = string.IsNullOrWhiteSpace(sample.SerialNumber) ? "" : "_" + sample.SerialNumber;
        string ts = sample.Timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return $"{safeName}{serial}_{ts}.csv";
    }

    private void WriteCsv(string path, SpectrumSample sample)
    {
        var wl = sample.Wavelengths;
        var inten = sample.Intensities;
        int n = Math.Min(wl.Length, inten.Length);
        var inv = CultureInfo.InvariantCulture;

        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("# Device,{0}", Name);
        writer.WriteLine("# SerialNumber,{0}", string.IsNullOrWhiteSpace(sample.SerialNumber) ? "" : sample.SerialNumber);
        writer.WriteLine("# Timestamp,{0}", sample.Timestamp.ToString("o", inv));
        writer.WriteLine("# IntegrationTimeMs,{0}", IntegrationTimeMs.ToString(inv));
        writer.WriteLine("# AverageCount,{0}", AverageCount.ToString(inv));
        writer.WriteLine("# IsTestMode,{0}", sample.IsSimulated);
        writer.WriteLine("# Points,{0}", n);
        writer.WriteLine("Wavelength (nm),Intensity");
        for (int i = 0; i < n; i++)
        {
            writer.Write(wl[i].ToString("R", inv));
            writer.Write(',');
            writer.WriteLine(inten[i].ToString("R", inv));
        }
    }

    private void RaiseCanExec()
    {
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        ApplyParamsCommand.RaiseCanExecuteChanged();
        SaveCsvCommand.RaiseCanExecuteChanged();
    }

    private void DetachDevice()
    {
        if (_device is null) return;
        _device.StatusChanged     -= OnStatusChanged;
        _device.ErrorOccurred     -= OnErrorOccurred;
        _device.DllNotFound       -= OnDllNotFound;
        _device.SpectrumAvailable -= OnSpectrumAvailable;
        _device.Dispose();
        _device = null;
    }

    public void Dispose() => DetachDevice();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
