using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Aqst.OesSpectrometer;
using Aqst.OesSpectrometer.Models;
using OxyPlot;

namespace OesTest;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    public MainViewModel()
    {
        Device1 = new DeviceViewModel("OES #1", OxyColors.SteelBlue);
        Device2 = new DeviceViewModel("OES #2", OxyColors.OrangeRed);

        Device1.PropertyChanged += OnDevicePropertyChanged;
        Device2.PropertyChanged += OnDevicePropertyChanged;

        ConnectBothCommand    = new RelayCommand(async () => await ConnectBothAsync(),    () => !IsBusy && !(Device1.IsConnected && Device2.IsConnected));
        DisconnectBothCommand = new RelayCommand(async () => await DisconnectBothAsync(), () => !IsBusy && (Device1.IsConnected || Device2.IsConnected));
    }

    public DeviceViewModel Device1 { get; }
    public DeviceViewModel Device2 { get; }

    public RelayCommand ConnectBothCommand { get; }
    public RelayCommand DisconnectBothCommand { get; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) RaiseCanExec(); }
    }

    private string _statusMessage = "Ready";
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }

    /// <summary>
    /// Mirror Python OpenMultiDevices: enumerate USB once, open every connected handle,
    /// hand one to each <see cref="DeviceViewModel"/>. If either device is in
    /// ForceTestMode, that slot falls back to the per-device <c>ConnectAsync</c> path so
    /// the test-mode simulator still works.
    /// </summary>
    private async Task ConnectBothAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Connecting both…";
        try
        {
            // Only enumerate USB when a slot actually wants a USB device — test-mode and Ethernet slots
            // never consume a discovery handle (Ethernet opens its IP directly via the standalone path).
            bool needHardware = SlotNeedsUsb(Device1) || SlotNeedsUsb(Device2);
            var handles = needHardware
                ? OesDiscovery.OpenAllDevices().ToList()
                : new System.Collections.Generic.List<OpenedHandle>();

            Debug.WriteLine($"ConnectBothAsync: discovered {handles.Count} hardware handle(s)");

            int hwIdx = 0;
            if (await ConnectSlotAsync(Device1, handles, hwIdx)) hwIdx++;
            if (await ConnectSlotAsync(Device2, handles, hwIdx)) hwIdx++;

            // Anything left over (more than 2 devices plugged in) — close it; we only support a pair.
            for (int i = hwIdx; i < handles.Count; i++)
            {
                Debug.WriteLine($"ConnectBothAsync: closing unused handle #{i}");
                OesDiscovery.CloseHandle(handles[i]);
            }

            // Summarize each slot's actual outcome — the raw USB-handle count hides Ethernet slots, which
            // open by IP through the standalone path and never consume a discovery handle.
            int hwUsed = Math.Min(hwIdx, handles.Count);
            StatusMessage = $"Connect Both done. {DescribeSlot(Device1)}; {DescribeSlot(Device2)}. " +
                            $"(USB handles used {hwUsed}/{handles.Count})";
        }
        catch (Exception ex)
        {
            StatusMessage = "Connect Both error: " + ex.Message;
            MessageBox.Show(ex.ToString(), "Connect Both failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A slot needs a USB discovery handle only when it is neither test-mode nor Ethernet.</summary>
    private static bool SlotNeedsUsb(DeviceViewModel slot) =>
        !slot.ForceTestMode && slot.ConnectionType != OesConnectionType.Ethernet;

    /// <summary>Human-readable per-slot connection outcome, including the transport used.</summary>
    private static string DescribeSlot(DeviceViewModel slot)
    {
        string transport = slot.ConnectionType == OesConnectionType.Ethernet
            ? $"Ethernet {(string.IsNullOrWhiteSpace(slot.IpAddress) ? "" : slot.IpAddress)}".TrimEnd()
            : "USB";
        if (!slot.IsConnected) return $"{slot.Name}: not connected";
        if (slot.IsTestMode)   return $"{slot.Name}: {transport} → test mode";
        return $"{slot.Name}: {transport} connected ({slot.SerialNumber})";
    }

    /// <summary>
    /// Returns true if a hardware handle was consumed (caller advances the index).
    /// </summary>
    private static async Task<bool> ConnectSlotAsync(
        DeviceViewModel slot,
        System.Collections.Generic.List<OpenedHandle> handles,
        int hwIdx)
    {
        if (slot.IsConnected) return false;

        // Test-mode slot: don't consume a hardware handle.
        if (slot.ForceTestMode)
        {
            await slot.ConnectStandaloneAsync();
            return false;
        }

        // Ethernet slot: opened directly by IP through the standalone path — never adopt a USB handle.
        if (slot.ConnectionType == OesConnectionType.Ethernet)
        {
            await slot.ConnectStandaloneAsync();
            return false;
        }

        if (hwIdx < handles.Count)
        {
            await slot.AttachAsync(handles[hwIdx]);
            return true;
        }

        // No hardware left — fall back to whatever ConnectAsync produces (test mode, etc.).
        await slot.ConnectStandaloneAsync();
        return false;
    }

    private async Task DisconnectBothAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = "Disconnecting…";
        try
        {
            await Task.WhenAll(Device1.DisconnectAsync(), Device2.DisconnectAsync());
            StatusMessage = "Disconnected.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Disconnect Both error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceViewModel.IsConnected)
                           or nameof(DeviceViewModel.IsBusy)
                           or nameof(DeviceViewModel.ForceTestMode))
        {
            RaiseCanExec();
        }
    }

    private void RaiseCanExec()
    {
        ConnectBothCommand.RaiseCanExecuteChanged();
        DisconnectBothCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        Device1.PropertyChanged -= OnDevicePropertyChanged;
        Device2.PropertyChanged -= OnDevicePropertyChanged;
        Device1.Dispose();
        Device2.Dispose();
    }

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
