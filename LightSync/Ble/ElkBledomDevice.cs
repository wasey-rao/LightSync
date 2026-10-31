using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Foundation.Metadata;

namespace LightSync.Ble;

public enum LinkState { Disconnected, Searching, Connecting, Connected }

/// <summary>
/// Owns the BLE link to one ELK-BLEDOM controller. All radio work runs on a single background
/// loop: discrete commands go out in order, color updates are coalesced so only the newest color
/// is ever sent (at most one per <see cref="ColorIntervalMs"/>), and a dropped link is re-established
/// automatically.
/// </summary>
public sealed class ElkBledomDevice : IDisposable
{
    // Writes are fire-and-forget (write-without-response), so Windows queues them without limit.
    // A small gap keeps the controller's tiny receive buffer from overflowing.
    const int CommandGapMs = 12;
    static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(12);

    readonly object _gate = new();
    readonly LinkedList<(string? Key, byte[] Data)> _commands = new();
    readonly SemaphoreSlim _wake = new(0);
    (byte R, byte G, byte B)? _pendingColor;
    (byte R, byte G, byte B)? _lastColor;
    Task? _loop;
    ulong _address;
    bool _wantConnected;
    bool _disposed;
    int _generation;

    BluetoothLEDevice? _device;
    GattSession? _session;
    GattDeviceService? _service;
    GattCharacteristic? _writer;
    IDisposable? _connectionParameters;
    volatile bool _linkLost;

    public LinkState State { get; private set; }
    public string Status { get; private set; } = "Not connected";
    public bool WantsConnection { get { lock (_gate) return _wantConnected; } }
    public int ColorIntervalMs { get; set; } = 33;

    /// <summary>Raised from a background thread whenever <see cref="State"/> or <see cref="Status"/> changes.</summary>
    public event Action? StateChanged;

    public void Connect(ulong address)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (address != _address) { _address = address; _generation++; }
            _wantConnected = true;
            _loop ??= Task.Run(RunAsync);
        }
        Wake();
    }

    public void Disconnect()
    {
        lock (_gate) _wantConnected = false;
        Wake();
    }

    /// <summary>
    /// Queues a command. A queued command with the same <paramref name="coalesceKey"/> is replaced
    /// instead of appended, so dragging a slider never builds a backlog. <paramref name="replacesColor"/>
    /// drops any color that hasn't been sent yet (used for effects and power-off).
    /// </summary>
    public void Send(byte[] command, string? coalesceKey = null, bool replacesColor = false)
    {
        lock (_gate)
        {
            var existing = coalesceKey == null ? null : FindQueued(coalesceKey);
            if (existing != null) existing.Value = (coalesceKey, command);
            else _commands.AddLast((coalesceKey, command));

            if (replacesColor) _pendingColor = null;
            // The controller's color is no longer what we last sent, so don't skip the next one.
            _lastColor = null;
        }
        Wake();
    }

    public void SetColor(byte r, byte g, byte b)
    {
        lock (_gate) _pendingColor = (r, g, b);
        Wake();
    }

    public static async Task ScanAsync(TimeSpan duration, Action<ulong, string, short> onSeen, CancellationToken cancel = default)
    {
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) => onSeen(e.BluetoothAddress, e.Advertisement.LocalName?.Trim() ?? "", e.RawSignalStrengthInDBm);
        watcher.Start();
        try { await Task.Delay(duration, cancel); }
        catch (OperationCanceledException) { }
        finally { watcher.Stop(); }
    }

    LinkedListNode<(string? Key, byte[] Data)>? FindQueued(string key)
    {
        for (var node = _commands.First; node != null; node = node.Next)
            if (node.Value.Key == key) return node;
        return null;
    }

    void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    async Task RunAsync()
    {
        int failures = 0, connectedGeneration = -1;
        long lastWrite = 0, lastColorWrite = 0;

        while (true)
        {
            bool want; ulong address; int generation;
            lock (_gate)
            {
                if (_disposed) break;
                want = _wantConnected;
                address = _address;
                generation = _generation;
            }

            if (!want)
            {
                TearDown();
                connectedGeneration = -1;
                failures = 0;
                SetState(LinkState.Disconnected, "Not connected");
                lock (_gate) { _commands.Clear(); _pendingColor = null; }
                await _wake.WaitAsync();
                continue;
            }

            if (_writer == null || _linkLost || generation != connectedGeneration)
            {
                TearDown();
                if (await TryConnectAsync(address))
                {
                    failures = 0;
                    connectedGeneration = generation;
                    lock (_gate) _lastColor = null;
                }
                else
                {
                    failures++;
                    await BackoffAsync(Math.Min(2000 * failures, 15000), generation);
                }
                continue;
            }

            byte[]? packet = null;
            string? key = null;
            (byte R, byte G, byte B)? color = null;
            int waitMs = Timeout.Infinite;
            lock (_gate)
            {
                if (_commands.First is { } first)
                {
                    _commands.RemoveFirst();
                    (key, packet) = first.Value;
                }
                else if (_pendingColor is { } pending)
                {
                    if (pending == _lastColor)
                    {
                        _pendingColor = null;
                    }
                    else
                    {
                        long due = lastColorWrite + ColorIntervalMs - Environment.TickCount64;
                        if (due <= 0)
                        {
                            packet = ElkProtocol.Color(pending.R, pending.G, pending.B);
                            color = pending;
                            _pendingColor = null;
                            _lastColor = pending;
                        }
                        else waitMs = (int)due;
                    }
                }
            }

            if (packet == null)
            {
                await _wake.WaitAsync(waitMs);
                continue;
            }

            long gap = lastWrite + CommandGapMs - Environment.TickCount64;
            if (gap > 0) await Task.Delay((int)gap);

            bool ok = await WriteAsync(packet);
            lastWrite = Environment.TickCount64;
            if (color != null) lastColorWrite = lastWrite;
            if (!ok)
            {
                _linkLost = true;
                lock (_gate)
                {
                    if (color != null) { _pendingColor ??= color; _lastColor = null; }
                    else _commands.AddFirst((key, packet));
                }
            }
        }

        TearDown();
    }

    async Task BackoffAsync(int milliseconds, int generation)
    {
        long until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            await Task.Delay(100);
            lock (_gate)
                if (_disposed || !_wantConnected || _generation != generation) return;
        }
    }

    async Task<bool> TryConnectAsync(ulong address)
    {
        string mac = BleAddress.Format(address);
        try
        {
            if (await CheckRadioAsync() is { } radioProblem)
            {
                SetState(LinkState.Disconnected, radioProblem);
                return false;
            }

            SetState(LinkState.Connecting, $"Connecting to {mac}…");
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device == null)
            {
                // Windows only opens devices it has seen advertise recently.
                SetState(LinkState.Searching, "Searching for the strip…");
                if (!await WaitForAdvertisementAsync(address, SearchTimeout))
                {
                    SetState(LinkState.Disconnected, "Strip not found. Is it powered on, and is the phone app disconnected from it?");
                    return false;
                }
                SetState(LinkState.Connecting, $"Connecting to {mac}…");
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
                if (device == null)
                {
                    SetState(LinkState.Disconnected, "Windows could not open the strip.");
                    return false;
                }
            }

            _device = device;
            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            try
            {
                _session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
                _session.MaintainConnection = true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GATT session unavailable: {ex.Message}");
            }

            for (int attempt = 1; attempt <= 3 && _writer == null; attempt++)
            {
                var services = await device.GetGattServicesForUuidAsync(ElkProtocol.ServiceUuid, BluetoothCacheMode.Uncached);
                if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0)
                {
                    var service = services.Services[0];
                    var characteristics = await service.GetCharacteristicsForUuidAsync(ElkProtocol.WriteCharacteristicUuid, BluetoothCacheMode.Uncached);
                    if (characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0)
                    {
                        _service = service;
                        _writer = characteristics.Characteristics[0];
                        break;
                    }
                    service.Dispose();
                    AppLog.Write($"Control characteristic lookup failed ({characteristics.Status}).");
                }
                else
                {
                    AppLog.Write($"Service lookup attempt {attempt} failed ({services.Status}).");
                }
                await Task.Delay(600);
            }

            if (_writer == null)
            {
                SetState(LinkState.Disconnected, "Couldn't reach the strip. Make sure it's on, in range, and not connected to your phone.");
                return false;
            }

            RequestLowLatency(device);
            _linkLost = false;
            SetState(LinkState.Connected, $"{device.Name.Trim()} · {mac}");
            return true;
        }
        catch (Exception ex)
        {
            SetState(LinkState.Disconnected, $"Connection error: {ex.Message}");
            return false;
        }
    }

    async Task<bool> WriteAsync(byte[] packet)
    {
        var writer = _writer;
        if (writer == null) return false;
        try
        {
            var result = await writer.WriteValueWithResultAsync(packet.AsBuffer(), GattWriteOption.WriteWithoutResponse);
            if (result.Status == GattCommunicationStatus.Success) return true;
            AppLog.Write($"Write failed ({result.Status}).");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Write error: {ex.Message}");
        }
        return false;
    }

    static async Task<string?> CheckRadioAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            if (adapter == null) return "No Bluetooth adapter found.";
            if (!adapter.IsLowEnergySupported) return "This Bluetooth adapter doesn't support Bluetooth LE.";
            var radio = await adapter.GetRadioAsync();
            if (radio != null && radio.State != RadioState.On) return "Bluetooth is turned off.";
        }
        catch (Exception ex)
        {
            AppLog.Write($"Bluetooth radio check failed: {ex.Message}");
        }
        return null;
    }

    static async Task<bool> WaitForAdvertisementAsync(ulong address, TimeSpan timeout)
    {
        var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) => { if (e.BluetoothAddress == address) found.TrySetResult(true); };
        watcher.Stopped += (_, _) => found.TrySetResult(false);
        watcher.Start();
        try { return await found.Task.WaitAsync(timeout); }
        catch (TimeoutException) { return false; }
        finally { watcher.Stop(); }
    }

    void RequestLowLatency(BluetoothLEDevice device)
    {
        // Windows 11+: ask for a short connection interval so color changes land sooner.
        if (!ApiInformation.IsTypePresent("Windows.Devices.Bluetooth.BluetoothLEPreferredConnectionParameters")) return;
        try
        {
            var request = device.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
            _connectionParameters = request;
            AppLog.Write($"Low-latency connection requested ({request.Status}).");
        }
        catch (Exception ex)
        {
            AppLog.Write($"Low-latency connection request failed: {ex.Message}");
        }
    }

    void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected || _writer == null) return;
        _linkLost = true;
        SetState(LinkState.Connecting, "Connection lost, reconnecting…");
        Wake();
    }

    void TearDown()
    {
        if (_device != null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _writer = null;
        SafeDispose(ref _connectionParameters);
        SafeDispose(ref _service);
        SafeDispose(ref _session);
        SafeDispose(ref _device);
        _linkLost = false;
    }

    static void SafeDispose<T>(ref T? value) where T : class, IDisposable
    {
        try { value?.Dispose(); } catch { /* the link is going away anyway */ }
        value = null;
    }

    void SetState(LinkState state, string status)
    {
        if (State == state && Status == status) return;
        State = state;
        Status = status;
        AppLog.Write(status);
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        Task? loop;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _wantConnected = false;
            loop = _loop;
        }
        Wake();
        if (loop == null) TearDown();
        else try { loop.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
    }
}
