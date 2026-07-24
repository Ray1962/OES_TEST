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
        PlotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Title = "Intensity (a.u.)",
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = OxyColor.FromRgb(0xE5, 0xE5, 0xE5),
            MinorGridlineStyle = LineStyle.Dot,
            MinorGridlineColor = OxyColor.FromRgb(0xF0, 0xF0, 0xF0),
        });
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

    private bool _forceTestMode = true;
    public bool ForceTestMode { get => _forceTestMode; set => Set(ref _forceTestMode, value); }

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
        private set { if (Set(ref _isConnected, value)) RaiseCanExec(); }
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
        private set { if (Set(ref _isBusy, value)) RaiseCanExec(); }
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
        _device.ForceTestMode(ForceTestMode);
    }

    private void ApplyConnectResult(bool ok)
    {
        if (ok && _device is not null)
        {
            IsConnected = true;
            SerialNumber = string.IsNullOrWhiteSpace(_device.DeviceInfo.SerialNumber)
                ? "—" : _device.DeviceInfo.SerialNumber;
            FrameSize = _device.DeviceInfo.FrameSize;
            IsTestMode = _device.DeviceInfo.IsTestMode;
            StatusMessage = $"Connected{(IsTestMode ? " (Test Mode)" : "")}";
        }
        else
        {
            StatusMessage = "Connect failed: " + (_device?.LastConnectionAttemptResult ?? "no device");
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
        IntegrationTimeMs = IntegrationTimeMs,
        AverageCount      = AverageCount,
        PollingIntervalMs = PollingIntervalMs,
        ForceTestMode     = ForceTestMode,
    };

    private void OnStatusChanged(object? sender, DeviceConnectionStatus s) =>
        _dispatcher.BeginInvoke(() => Status = s);

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
        for (int i = 0; i < n; i++)
            points.Add(new DataPoint(wl[i], inten[i]));

        LastFrameTime = sample.Timestamp;
        IsTestMode = sample.IsTestMode;
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
        writer.WriteLine("# IsTestMode,{0}", sample.IsTestMode);
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
