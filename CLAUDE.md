# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-project WPF (.NET 8, x64, Windows-only) test harness that drives **two** OES spectrometers side by side through the `Aqst.OesSpectrometer` NuGet package: connect, set acquisition parameters, live-plot the spectrum with OxyPlot, and export a single frame to CSV.

## Build / run

The session shell is WSL but the app is Windows-only WPF — use the Windows `dotnet.exe`, and pass Windows-style paths where it matters:

```bash
dotnet.exe build OES_TEST.sln -c Debug        # or -c Release; x64 is the only platform
dotnet.exe run --project src/OES_TEST/OES_TEST.csproj
# built exe: src/OES_TEST/bin/x64/Debug/net8.0-windows/OesTest.exe
```

There are no tests, linters, or CI in this repo.

`nuget.config` clears all sources and adds a **local folder feed** at `C:\Users\infor\source\repos\Ray1962\LocalPackages` (where the `Aqst.OesSpectrometer.*.nupkg` files live) plus nuget.org. Bumping the `Aqst.OesSpectrometer` version means dropping a new `.nupkg` in that folder first; if restore can't find the version, that folder is the reason.

The `FlattenOesNativeDlls` target in `OES_TEST.csproj` copies `UserApplication.dll`, `SiUSBXp.dll` and `libsodium.dll` from `runtimes/win-x64/native/` up to the output root — the package's DLL resolver only searches the app base directory. Do not delete it; without it the app silently falls back to test mode.

`libsodium.dll` is an **import of the 0.4.x `UserApplication.dll`** and must sit beside it. The 0.4.0 package omitted it, so `LoadLibrary` failed with `ERROR_MOD_NOT_FOUND` and every connect fell back to test mode while the file itself was clearly present — the tell is `LastConnectionAttemptResult` reading `UserApplication.dll load failed at '<path>' - test mode activated`. Fixed in **0.4.1**; if the native DLL is ever updated again, re-check its import table.

### Per-device hardware quirks

Not every spectrometer supports every correction in the SDK pipeline. `OS361AC55042227` (350–650 nm) returns `0x4` from `UAI_BackgroundRemove` for every integration time and average count — no background-calibration data in ROM — while `OS361AC55035689` (365–545 nm) accepts the call. Since **0.4.2** the SDK latches that, warns once through `ErrorOccurred`, and keeps streaming raw intensities; before that the panel showed a connected device with an all-zero spectrum and acquisition died after five frames.

**Background Remove is opt-in per panel and connect-time only (0.4.3).** Each `DevicePanel` has a "Background Remove" checkbox bound to `DeviceViewModel.EnableBackgroundRemove` (default **off**, matching the SDK 0.4.3 default). It is a **connect-time parameter**: `BuildParameters()` bakes it into `OesParameters` inside `CreateDeviceWrapper()`, so it only reaches the device if set *before* Connect. To make that unmissable, the checkbox is disabled once connected/busy via `IsBackgroundRemoveEditable` (`!IsConnected && !IsBusy`) — toggling it on a live device is a no-op (the flag never reaches the device and the probe never re-runs), which is exactly the trap that made it "have no effect" in an earlier build. When checked before connecting, the connect paths (`ConnectStandaloneAsync` / `AttachAsync`) call `ProbeBackgroundRemoveIfEnabledAsync()` right after a successful connect: it invokes `IOesSpectrometer.ProbeBackgroundRemoveAsync()` and, on `BackgroundRemoveSupport.Unsupported`, pops a warning `MessageBox`, clears the checkbox, and lets acquisition stream raw intensities. The probe is skipped in test mode.

Two consequences the app relies on: failed frames no longer raise `SpectrumAvailable` (so the plot is never fed a stale/zero buffer), and a self-aborting acquisition loop leaves the `Acquiring` status — `DeviceViewModel.OnStatusChanged` uses that to clear `IsAcquiring`, since nothing else tells the panel the loop stopped.

## Architecture

MVVM by hand — no MVVM framework. `RelayCommand` is a minimal `ICommand` with an explicit `RaiseCanExecuteChanged()` (there is no `CommandManager` requery), so **any state change that affects a command's `CanExecute` must call `RaiseCanExec()`**; that's why most setters route through `Set(...)` and then re-raise.

- `MainWindow.xaml` instantiates `MainViewModel` directly in `Window.DataContext` and hosts two `DevicePanel` user controls whose `DataContext` is bound to `Device1` / `Device2`. `DevicePanel` has no code-behind logic — it is a pure template over `DeviceViewModel`.
- `DeviceViewModel` owns one `OesSpectrometer`, its `PlotModel`/`LineSeries`, the per-device parameters (integration time, average count, polling interval, `ForceTestMode`), and all six device commands.
- `MainViewModel` owns the two `DeviceViewModel`s and coordinates the "Connect Both" / "Disconnect Both" pair, listening to their `PropertyChanged` to keep its own commands enabled correctly.

### The multi-device connect rule (the central constraint)

`OesSpectrometer.ConnectAsync()` always opens native device **index 0**. Calling it on two instances collides on the same physical device. So:

- **Single/test-mode slot** → `DeviceViewModel.ConnectStandaloneAsync()` (wraps `ConnectAsync`). This is what the per-panel *Connect* button does; it is only safe when one real device is in play.
- **Both real devices** → `MainViewModel.ConnectBothAsync()` calls `OesDiscovery.OpenAllDevices()` **once**, then hands each slot a distinct `OpenedHandle` via `DeviceViewModel.AttachAsync(handle)`. Handles not handed off must be closed with `OesDiscovery.CloseHandle`; after a successful `AttachAsync` ownership transfers to the wrapper (closed on `DisconnectAsync`/`Dispose`).

`ConnectBothAsync` also honors per-slot `ForceTestMode`: a test-mode slot never consumes a hardware handle, and a slot with no handle left falls back to the standalone path.

The `use-multi-oes` and `create-oes` skills document this package's API in more depth; consult them before changing connect/attach code.

### Threading

`OesSpectrometer` raises `SpectrumAvailable`, `StatusChanged`, `ErrorOccurred`, and `DllNotFound` on background threads. `DeviceViewModel` captures `Dispatcher.CurrentDispatcher` at construction and marshals every handler with `BeginInvoke`. Keep that pattern for any new event handler — plot mutation and property raises must happen on the UI thread.

### Test mode

`ForceTestMode` defaults to **false** on both slots, so the app targets real hardware out of the box; check "Force Test Mode" per panel to work against the package's simulator instead. Note `ConnectBothAsync` skips USB enumeration entirely when *both* slots are in test mode. The package also drops into test mode on its own when `UserApplication.dll` cannot be loaded (surfaced via the `DllNotFound` event and `DeviceInfo.IsTestMode`).

### Per-panel acquire method

Each `DevicePanel` has an **Acquire** dropdown (`DeviceViewModel.AcquireMode`, `OesAcquireMode`, default `HardwareAverage`) and an **Avg mode** dropdown (`AverageMode`, `OesAverageMode`, default `Hardware`), both flowing through `BuildParameters()`. Unlike the connect-time settings they are **hot-applied**: `UpdateParametersAsync` pushes them to the live device, so the selectors stay editable while connected and take effect on **Apply**. Pick `Oneshot` on a network OES that shows segmented/torn frames under `HardwareAverage`; pick `Avg mode = Software` when the module's hardware averager shifts/broadens peaks (observed on the Z5/Ethernet OES #2) — software averaging acquires N single frames and averages them element-wise. Compare either without reconnecting.

### Per-panel connection type (USB / Ethernet)

Each `DevicePanel` has a **Type** selector (`DeviceViewModel.ConnectionType`, default `Usb`) and, when Ethernet is picked, an **IP Address** textbox (`IpAddress`) that shows via `IsEthernetSelected` + `BooleanToVisibilityConverter`. Both flow through `BuildParameters()` and are connect-time only, so they share the `IsPreConnectEditable` (`!IsConnected && !IsBusy`) lock — set them before Connect. **Ethernet opens by IP directly** (`OesSpectrometer.ConnectAsync` → `ConnectDeviceEthernet`), so an Ethernet slot must go through the standalone path: `ConnectBothAsync`/`SlotNeedsUsb` never hand it a `OesDiscovery.OpenAllDevices` (USB) handle, and USB enumeration is skipped when neither slot needs USB. Use the panel's own **Connect** button for an Ethernet device.

### Conventions

- `RootNamespace`/`AssemblyName` is `OesTest` while the folder and solution are `OES_TEST` — namespace is `OesTest`, not `OES_TEST`.
- CSV writing uses `CultureInfo.InvariantCulture` and `"R"` round-trip formatting, with `#`-prefixed metadata header lines before the `Wavelength (nm),Intensity` row.
- `Dispose()` chains down: window `Closed` → `MainViewModel.Dispose()` → each `DeviceViewModel.Dispose()` → unsubscribe + `OesSpectrometer.Dispose()`.
