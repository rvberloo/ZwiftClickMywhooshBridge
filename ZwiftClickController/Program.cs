using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

class Program
{
    private const byte RideControllerNotificationOpcode = 0x07;
    private const byte RideVendorMessageOpcode = 0xFF;
    private const byte RideBatteryNotificationOpcode = 0x2A;
    private const byte RideOnOpcode = 0x52;

    private static readonly List<ControllerConnection> Connections = new();
    private static readonly object ConnectionsLock = new();
    private static readonly SemaphoreSlim ConnectGate = new(1, 1);
    private static readonly Dictionary<string, BleCandidate> LastKnownByLabel = new(StringComparer.Ordinal);
    private static readonly BleDiscoveryService Discovery = new();
    private static readonly GattDiscoveryService Gatt = new();

    // Zwift Click terminology:
    // Minus = (-) button, Plus = (+) button.
    // The first connected Zwift Click uses Controller1,
    // the second connected Zwift Click uses Controller2.
    private static readonly ControllerMapping Controller1Mapping = new(
        "Controller1",
        ButtonAction.KeyI,
        ButtonAction.KeyK,
        ButtonAction.KeyLeftArrow,
        ButtonAction.KeyRightArrow);

    private static readonly ControllerMapping Controller2Mapping = new(
        "Controller2",
        ButtonAction.KeyI,
        ButtonAction.KeyK,
        ButtonAction.KeyLeftArrow,
        ButtonAction.KeyRightArrow);

    private static readonly ControllerMapping[] ControllerMappings = { Controller1Mapping, Controller2Mapping };

    // Shared ride-protocol button state — both BLE subscriptions receive the same packets,
    // so tracking state per-connection causes drift. One shared mask avoids duplicate/missed edges.
    private static uint SharedRidePressedMask = 0;
    private static readonly object SharedRideLock = new();

    static async Task Main()
    {
        try
        {
            Console.WriteLine("=== Zwift Click C# Controller ===");
            PrintMappings();

            await ReconnectMissingControllersAsync(printDiscoveryLog: true);

            if (GetConnectionCount() == 0)
            {
                Console.WriteLine("Could not connect to any Zwift Click controller.");
                return;
            }

            Console.WriteLine($"{GetConnectionCount()} controller(s) active.");

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(10000);
                    foreach (var connection in GetConnectionsSnapshot())
                    {
                        await SendRideOn(connection);
                    }

                    ReconnectSilentConnections(TimeSpan.FromSeconds(60));
                    if (GetConnectionCount() < ControllerMappings.Length)
                    {
                        await ReconnectMissingControllersAsync(printDiscoveryLog: false);
                    }
                }
            });

            Console.WriteLine("Listening for button presses. Press Enter to stop.");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error: {ex}");
        }
    }

    private static async Task SendRideOn(ControllerConnection connection)
    {
        try
        {
            var writer = new DataWriter();
            writer.WriteString("RideOn");
            await connection.ControlChar.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            connection.KeepAliveFailureCount = 0;
        }
        catch (Exception ex)
        {
            connection.KeepAliveFailureCount++;
            Console.WriteLine($"SendRideOn error for {connection.Label} ({connection.KeepAliveFailureCount}/3): {ex.Message}");

            if (connection.KeepAliveFailureCount >= 3 && IsActiveConnection(connection))
            {
                CleanupConnection(connection, "Keepalive write failed");
                _ = Task.Run(async () => await ReconnectMissingControllersAsync(printDiscoveryLog: false));
            }
        }
    }

    private static async Task ReconnectMissingControllersAsync(bool printDiscoveryLog)
    {
        await ConnectGate.WaitAsync();

        try
        {
            var missingMappings = GetMissingMappings();
            if (missingMappings.Count == 0)
            {
                return;
            }

            var usedAddresses = new HashSet<ulong>(GetConnectionsSnapshot().Select(c => c.Address));
            List<BleCandidate>? prioritized = null;

            foreach (var mapping in missingMappings)
            {
                var candidate = GetLastKnownCandidate(mapping.Label, usedAddresses);

                if (candidate != null && await TryConnectCandidateAsync(candidate, mapping))
                {
                    usedAddresses.Add(candidate.Address);
                    continue;
                }

                prioritized ??= await DiscoverCandidatesAsync(printDiscoveryLog);
                candidate = prioritized.FirstOrDefault(c => !usedAddresses.Contains(c.Address));

                if (candidate == null)
                {
                    Console.WriteLine($"No available BLE candidate for {mapping.Label}.");
                    break;
                }

                if (await TryConnectCandidateAsync(candidate, mapping))
                {
                    usedAddresses.Add(candidate.Address);
                }
            }

            if (prioritized is { Count: 0 })
            {
                Console.WriteLine("No Zwift Click controllers found for reconnect.");
            }
        }
        finally
        {
            ConnectGate.Release();
        }
    }

    private static async Task<bool> TryConnectCandidateAsync(BleCandidate candidate, ControllerMapping mapping)
    {
        Console.WriteLine($"Trying to connect {mapping.Label}: {candidate.Name} [{BleDiscoveryService.FormatAddress(candidate.Address)}]");
        var device = await Gatt.TryConnectAsync(candidate);
        if (device == null)
        {
            return false;
        }

        Console.WriteLine($"Connected to: {device.Name} [{BleDiscoveryService.FormatAddress(candidate.Address)}]");

        var selectedChars = await Gatt.FindCharacteristicsAsync(device);
        if (selectedChars.control == null || selectedChars.notify == null)
        {
            Console.WriteLine("No usable write/notify characteristics found on this device.");
            device.Dispose();
            return false;
        }

        var connection = new ControllerConnection
        {
            Label = mapping.Label,
            Address = candidate.Address,
            Device = device,
            ControlChar = selectedChars.control,
            NotifyChar = selectedChars.notify,
            Mapping = mapping,
            LastPressedMask = 0,
            LastRidePressedMask = 0,
            RideMinusMask = 0,
            RidePlusMask = 0,
            RideLeftMask = 0,
            RideRightMask = 0,
            LastNotificationUtc = DateTime.UtcNow,
            LastButtonNotificationUtc = DateTime.UtcNow,
            KeepAliveFailureCount = 0
        };

        var notifyStatus = await connection.NotifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);

        if (notifyStatus != GattCommunicationStatus.Success)
        {
            Console.WriteLine($"Failed to enable notifications for {connection.Label}: {notifyStatus}");
            device.Dispose();
            return false;
        }

        // Subscribe to indications on the sync TX characteristic (00000004).
        // The device uses this channel for handshake responses; subscribing signals readiness.
        if (selectedChars.syncTx != null)
        {
            var indicateStatus = await selectedChars.syncTx.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Indicate);
            if (indicateStatus != GattCommunicationStatus.Success)
            {
                Console.WriteLine($"Warning: could not enable indications for {connection.Label}: {indicateStatus}");
            }
        }

        connection.NotifyChar.ValueChanged += OnNotifyValueChanged;
        connection.Device.ConnectionStatusChanged += OnConnectionStatusChanged;

        ControllerConnection? existing;
        lock (ConnectionsLock)
        {
            existing = Connections.FirstOrDefault(c => c.Label == mapping.Label);
        }

        if (existing != null)
        {
            CleanupConnection(existing, "Replacing stale connection");
        }

        lock (ConnectionsLock)
        {
            Connections.Add(connection);
            LastKnownByLabel[mapping.Label] = candidate;
        }

        await SendRideOn(connection);

        // Zwift Click V2 (fc82 service): send the vendor init command so the device
        // skips the full crypto challenge and immediately starts sending button events.
        // Without this it sends a 0xFF len=103 public-key challenge and waits indefinitely.
        var v2Init = new DataWriter();
        v2Init.WriteByte(0xFF);
        v2Init.WriteByte(0x04);
        v2Init.WriteByte(0x00);
        await connection.ControlChar.WriteValueAsync(v2Init.DetachBuffer(), GattWriteOption.WriteWithoutResponse);

        Console.WriteLine($"{connection.Label} active on [{BleDiscoveryService.FormatAddress(candidate.Address)}]");
        return true;
    }

    private static async Task<List<BleCandidate>> DiscoverCandidatesAsync(bool printDiscoveryLog)
    {
        if (printDiscoveryLog)
        {
            Console.WriteLine("Scanning for BLE devices...");
        }

        var scanCandidates = await Discovery.ScanBleDevicesAsync(TimeSpan.FromSeconds(8));
        var registryCandidates = Discovery.GetPairedBluetoothDevicesFromRegistry();
        var deviceInfoCandidates = await Discovery.GetWindowsBleDevicesAsync();

        if (printDiscoveryLog)
        {
            Console.WriteLine($"Method 1 (scan): {scanCandidates.Count} candidates");
            foreach (var c in scanCandidates)
            {
                if (c.Name.Contains("click", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  - {c.Name} [{BleDiscoveryService.FormatAddress(c.Address)}] ({c.Source})");
                }
            }

            Console.WriteLine($"Method 2 (registry): {registryCandidates.Count} candidates");
            foreach (var c in registryCandidates)
            {
                Console.WriteLine($"  - {c.Name} [{BleDiscoveryService.FormatAddress(c.Address)}] ({c.Source})");
            }

            Console.WriteLine($"Method 3 (windows devices): {deviceInfoCandidates.Count} candidates");
            foreach (var c in deviceInfoCandidates)
            {
                Console.WriteLine($"  - {c.Name} [{BleDiscoveryService.FormatAddress(c.Address)}] ({c.Source})");
            }
        }

        return Discovery.MergeCandidates(scanCandidates, deviceInfoCandidates, registryCandidates)
            .Where(c => c.Name.Contains("click", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Address)
            .ToList();
    }

    private static void OnNotifyValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        ControllerConnection? connection;
        lock (ConnectionsLock)
        {
            connection = Connections.FirstOrDefault(c => ReferenceEquals(c.NotifyChar, sender));
        }

        if (connection == null)
        {
            return;
        }

        OnButtonChanged(connection, args);
    }

    private static void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            return;
        }

        ControllerConnection? disconnected;
        lock (ConnectionsLock)
        {
            disconnected = Connections.FirstOrDefault(c => ReferenceEquals(c.Device, sender));
        }

        if (disconnected == null)
        {
            return;
        }

        CleanupConnection(disconnected, "Bluetooth disconnected");
        _ = Task.Run(async () =>
        {
            // Give Windows BLE time to fully tear down the old connection before reconnecting.
            await Task.Delay(2000);
            await ReconnectMissingControllersAsync(printDiscoveryLog: false);
        });
    }

    private static void CleanupConnection(ControllerConnection connection, string reason)
    {
        var removed = false;
        lock (ConnectionsLock)
        {
            removed = Connections.Remove(connection);
        }

        if (!removed)
        {
            return;
        }

        Console.WriteLine($"{connection.Label} disconnected: {reason}");

        // Clear shared ride state so the first press after reconnect isn't eaten by stale bits.
        lock (SharedRideLock)
        {
            SharedRidePressedMask = 0;
        }

        connection.NotifyChar.ValueChanged -= OnNotifyValueChanged;
        connection.Device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        connection.Device.Dispose();
    }

    private static bool IsActiveConnection(ControllerConnection connection)
    {
        lock (ConnectionsLock)
        {
            return Connections.Contains(connection);
        }
    }

        private static void ReconnectSilentConnections(TimeSpan staleThreshold)
    {
        var now = DateTime.UtcNow;
        foreach (var connection in GetConnectionsSnapshot())
        {
            if (connection.Device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                CleanupConnection(connection, "Connection status not connected");
                continue;
            }

            // Device totally silent — no notifications of any kind.
            if (now - connection.LastNotificationUtc > staleThreshold)
            {
                CleanupConnection(connection, $"No notifications for {(int)staleThreshold.TotalSeconds}s");
                continue;
            }

            // Auth-timeout: device is alive (sending battery pings) but button notifications
            // stopped. Reconnect to re-trigger the RideOn + V2 handshake.
            if (connection.HasSawButtonNotification
                && now - connection.LastButtonNotificationUtc > TimeSpan.FromSeconds(30))
            {
                CleanupConnection(connection, "Button notifications stopped (auth timeout)");
            }
        }
    }

    private static BleCandidate? GetLastKnownCandidate(string label, HashSet<ulong> usedAddresses)
    {
        lock (ConnectionsLock)
        {
            if (!LastKnownByLabel.TryGetValue(label, out var candidate))
            {
                return null;
            }

            if (usedAddresses.Contains(candidate.Address))
            {
                return null;
            }

            return candidate;
        }
    }

    private static int GetConnectionCount()
    {
        lock (ConnectionsLock)
        {
            return Connections.Count;
        }
    }

    private static List<ControllerConnection> GetConnectionsSnapshot()
    {
        lock (ConnectionsLock)
        {
            return Connections.ToList();
        }
    }

    private static List<ControllerMapping> GetMissingMappings()
    {
        var connectedLabels = new HashSet<string>(GetConnectionsSnapshot().Select(c => c.Label), StringComparer.Ordinal);
        return ControllerMappings.Where(m => !connectedLabels.Contains(m.Label)).ToList();
    }

    private static void OnButtonChanged(ControllerConnection connection, GattValueChangedEventArgs args)
    {
        try
        {
            connection.LastNotificationUtc = DateTime.UtcNow;

            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);

            if (data.Length == 0)
            {
                return;
            }

            var messageType = data[0];
            Console.WriteLine($"[RAW] {connection.Label}: type=0x{messageType:X2} len={data.Length}");
            if (messageType == ClickProtocolParser.EmptyMessageType
                || messageType == ClickProtocolParser.BatteryLevelType
                || messageType == RideVendorMessageOpcode
                || messageType == RideBatteryNotificationOpcode
                || messageType == RideOnOpcode)
            {
                return;
            }

            if (messageType == ClickProtocolParser.RideNotificationMessageType || messageType == RideControllerNotificationOpcode)
            {
                connection.LastButtonNotificationUtc = DateTime.UtcNow;
                connection.HasSawButtonNotification = true;
                HandleRideStyleNotification(connection, data.AsSpan(1));
                return;
            }

            if (messageType != ClickProtocolParser.ClickNotificationMessageType)
            {
                Console.WriteLine($"{connection.Label}: unknown message type=0x{messageType:X2}");
                return;
            }

            var pressed = ClickProtocolParser.ParseClickPressedMask(data.AsSpan(1));
            connection.LastButtonNotificationUtc = DateTime.UtcNow;
            if (pressed == connection.LastPressedMask)
            {
                return;
            }

            connection.LastPressedMask = pressed;
            if (pressed == 0)
            {
                return;
            }

            Console.WriteLine($"{connection.Label}: raw={BitConverter.ToString(data)} pressed={pressed:X2}");

            if ((pressed & 0x01) != 0)
            {
                Console.WriteLine($"{connection.Label}: Minus (-)");
                InputActionExecutor.Execute(connection.Mapping.Minus);
            }
            else if ((pressed & 0x02) != 0)
            {
                Console.WriteLine($"{connection.Label}: Plus (+)");
                InputActionExecutor.Execute(connection.Mapping.Plus);
            }
            else if ((pressed & 0x04) != 0)
            {
                Console.WriteLine($"{connection.Label}: Shift Up");
                InputActionExecutor.Execute(connection.Mapping.ShiftUp);
            }
            else if ((pressed & 0x08) != 0)
            {
                Console.WriteLine($"{connection.Label}: Shift Down");
                InputActionExecutor.Execute(connection.Mapping.ShiftDown);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Button processing error: {ex.Message}");
        }
    }

    private static void HandleRideStyleNotification(ControllerConnection connection, ReadOnlySpan<byte> message)
    {
        if (!ClickProtocolParser.TryParseRidePressedMask(message, out var pressedNow))
        {
            return;
        }

        uint prevMask, newPressed;
        lock (SharedRideLock)
        {
            prevMask = SharedRidePressedMask;
            newPressed = pressedNow & ~prevMask;
            SharedRidePressedMask = pressedNow;
        }

        if (newPressed == 0)
        {
            return;
        }

        // Controller1 bitmasks (Zwift Click - left side, Ride protocol)
        const uint c1MinusBtn = 0x00100;
        const uint c1LeftBtn  = 0x00002;
        const uint c1RightBtn = 0x00004;

        // Controller2 bitmasks (Zwift Click - right side, Ride protocol)
        const uint c2PlusMask = 0x01000 | 0x02000;
        const uint c2ABtn     = 0x00010;
        const uint c2BBtn     = 0x00020;
        const uint c2ZBtn     = 0x00080;

        var handled = false;

        if ((newPressed & c1MinusBtn) != 0)
        {
            Console.WriteLine("Controller1: Minus (-)");
            InputActionExecutor.Execute(Controller1Mapping.Minus);
            handled = true;
        }

        if ((newPressed & c1LeftBtn) != 0)
        {
            Console.WriteLine("Controller1: Left");
            InputActionExecutor.Execute(Controller1Mapping.ShiftUp);
            handled = true;
        }

        if ((newPressed & c1RightBtn) != 0)
        {
            Console.WriteLine("Controller1: Right");
            InputActionExecutor.Execute(Controller1Mapping.ShiftDown);
            handled = true;
        }

        // Only fire on the first arriving plus-bit; ignore subsequent packets that add the second bit.
        if ((newPressed & c2PlusMask) != 0 && (prevMask & c2PlusMask) == 0)
        {
            Console.WriteLine("Controller2: Plus (+)");
            InputActionExecutor.Execute(Controller2Mapping.Plus);
            handled = true;
        }

        if ((newPressed & c2ABtn) != 0)
        {
            Console.WriteLine("Controller2: A");
            InputActionExecutor.Execute(ButtonAction.CtrlLeft);
            handled = true;
        }

        if ((newPressed & c2BBtn) != 0)
        {
            Console.WriteLine("Controller2: B");
            InputActionExecutor.Execute(ButtonAction.SpaceBar);
            handled = true;
        }

        if ((newPressed & c2ZBtn) != 0)
        {
            Console.WriteLine("Controller2: Z");
            InputActionExecutor.Execute(ButtonAction.CtrlRight);
            handled = true;
        }

        if (!handled)
        {
            Console.WriteLine($"{connection.Label}: unmapped ride bits 0x{newPressed:X}");
        }
    }

    private static void PrintMappings()
    {
        Console.WriteLine($"{Controller1Mapping.Label}: Minus(-)={Controller1Mapping.Minus}, Plus(+)={Controller1Mapping.Plus}, Left={Controller1Mapping.ShiftUp}, Right={Controller1Mapping.ShiftDown}");
        Console.WriteLine($"{Controller2Mapping.Label}: Plus(+)={Controller2Mapping.Plus}, A=CtrlLeft, B=Space, Z=CtrlRight");
    }
}
