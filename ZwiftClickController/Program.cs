using System.Globalization;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;
using WindowsInput;
using WindowsInput.Native;

class Program
{
    private static readonly Guid ServiceUuid = new("00000001-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid NotifyUuid = new("00000002-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid ControlUuid = new("00000003-19ca-4651-86e5-fa29dcdd09d1");
    private const byte ClickNotificationMessageType = 0x37;
    private const byte RideNotificationMessageType = 0x23;
    private const byte EmptyMessageType = 0x15;
    private const byte BatteryLevelType = 0x19;

    private static readonly InputSimulator Sim = new();
    private static readonly List<ControllerConnection> Connections = new();

    private enum ButtonAction
    {
        None,
        KeyLeftBracket,
        KeyRightBracket,
        KeyLeftArrow,
        KeyRightArrow,
        KeyI,
        KeyK,
        SpotifyPlayPause,
        SpotifyNextTrack,
        SpotifyPreviousTrack,
        SpotifyVolumeUp,
        SpotifyVolumeDown
    }

    private sealed record ControllerMapping(
        string Label,
        ButtonAction Down,
        ButtonAction Up,
        ButtonAction ShiftUp,
        ButtonAction ShiftDown);

    private sealed class ControllerConnection
    {
        public required string Label { get; init; }
        public required ulong Address { get; init; }
        public required BluetoothLEDevice Device { get; init; }
        public required GattCharacteristic ControlChar { get; init; }
        public required GattCharacteristic NotifyChar { get; init; }
        public required ControllerMapping Mapping { get; init; }
        public byte LastPressedMask { get; set; }
        public uint LastRidePressedMask { get; set; }
        public uint RideDownMask { get; set; }
        public uint RideUpMask { get; set; }
        public uint RideLeftMask { get; set; }
        public uint RideRightMask { get; set; }
    }

    // Zwift Click terminology:
    // Down = Minus (-) button, Up = Plus (+) button.
    // The first connected Zwift Click uses Controller1,
    // the second connected Zwift Click uses Controller2.
    private static readonly ControllerMapping Controller1Mapping = new(
        "Controller1",
        ButtonAction.KeyK,
        ButtonAction.KeyI,
        ButtonAction.KeyLeftArrow,
        ButtonAction.KeyRightArrow);

    private static readonly ControllerMapping Controller2Mapping = new(
        "Controller2",
        ButtonAction.KeyK,
        ButtonAction.KeyI,
        ButtonAction.KeyLeftArrow,
        ButtonAction.KeyRightArrow);

    static async Task Main()
    {
        try
        {
            Console.WriteLine("=== Zwift Click C# Controller ===");
            Console.WriteLine("Zoeken naar BLE apparaten...");
            PrintMappings();

            var scanCandidates = await ScanBleDevicesAsync(TimeSpan.FromSeconds(8));
            Console.WriteLine($"Methode 1 (scan): {scanCandidates.Count} kandidaten");
            foreach (var c in scanCandidates)
            {
                if (c.Name.Contains("click", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  - {c.Name} [{FormatAddress(c.Address)}] ({c.Source})");
                }
            }

            var registryCandidates = GetPairedBluetoothDevicesFromRegistry();
            Console.WriteLine($"Methode 2 (registry): {registryCandidates.Count} kandidaten");
            foreach (var c in registryCandidates)
            {
                Console.WriteLine($"  - {c.Name} [{FormatAddress(c.Address)}] ({c.Source})");
            }

            var deviceInfoCandidates = await GetWindowsBleDevicesAsync();
            Console.WriteLine($"Methode 3 (windows devices): {deviceInfoCandidates.Count} kandidaten");
            foreach (var c in deviceInfoCandidates)
            {
                Console.WriteLine($"  - {c.Name} [{FormatAddress(c.Address)}] ({c.Source})");
            }

            var merged = scanCandidates
                .Concat(deviceInfoCandidates)
                .Concat(registryCandidates)
                .GroupBy(c => c.Address)
                .Select(MergeCandidateGroup)
                .ToList();

            if (merged.Count == 0)
            {
                Console.WriteLine("Geen BLE kandidaten gevonden.");
                Console.WriteLine("Controleer of de controller in pairing mode staat en dicht bij de pc is.");
                return;
            }

            var prioritized = merged
                .Where(c => c.Name.Contains("click", StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Address)
                .ToList();

            if (prioritized.Count == 0)
            {
                Console.WriteLine("Geen Zwift Click controllers gevonden.");
                return;
            }

            var mappings = new[] { Controller1Mapping, Controller2Mapping };
            foreach (var candidate in prioritized.Take(mappings.Length))
            {
                Console.WriteLine($"Probeer verbinden: {candidate.Name} [{FormatAddress(candidate.Address)}]");
                var device = await TryConnectAsync(candidate);
                if (device == null)
                {
                    continue;
                }

                Console.WriteLine($"Verbonden met: {device.Name} [{FormatAddress(candidate.Address)}]");

                var selectedChars = await FindCharacteristicsAsync(device);
                if (selectedChars.control != null && selectedChars.notify != null)
                {
                    var mapping = mappings[Connections.Count];
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
                        RideDownMask = 0,
                        RideUpMask = 0,
                        RideLeftMask = 0,
                        RideRightMask = 0
                    };

                    var notifyStatus = await connection.NotifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify);

                    if (notifyStatus != GattCommunicationStatus.Success)
                    {
                        Console.WriteLine($"Notifications inschakelen mislukt voor {connection.Label}: {notifyStatus}");
                        device.Dispose();
                        continue;
                    }

                    connection.NotifyChar.ValueChanged += (_, args) => OnButtonChanged(connection, args);
                    Connections.Add(connection);
                    await SendRideOn(connection);
                    Console.WriteLine($"{connection.Label} actief op [{FormatAddress(candidate.Address)}]");
                    continue;
                }

                Console.WriteLine("Geen bruikbare write/notify characteristics gevonden op dit apparaat.");
                device.Dispose();
            }

            if (Connections.Count == 0)
            {
                Console.WriteLine("Kon niet verbinden met een Zwift Click controller.");
                return;
            }

            Console.WriteLine($"{Connections.Count} controller(s) actief.");

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(30000);
                    foreach (var connection in Connections)
                    {
                        await SendRideOn(connection);
                    }
                }
            });

            Console.WriteLine("Luisteren naar knoppen. Druk Enter om te stoppen.");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Onverwachte fout: {ex}");
        }
    }

    private static async Task<BluetoothLEDevice?> TryConnectAsync(BleCandidate candidate)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                BluetoothLEDevice? device;
                if (!string.IsNullOrWhiteSpace(candidate.DeviceId))
                {
                    device = await BluetoothLEDevice.FromIdAsync(candidate.DeviceId);
                }
                else
                {
                    device = await BluetoothLEDevice.FromBluetoothAddressAsync(candidate.Address);
                }

                if (device == null)
                {
                    await Task.Delay(300 * attempt);
                    continue;
                }

                var access = await device.RequestAccessAsync();
                if (access != DeviceAccessStatus.Allowed)
                {
                    Console.WriteLine($"Toegang geweigerd voor {candidate.Name}: {access}");
                    device.Dispose();
                    await Task.Delay(300 * attempt);
                    continue;
                }

                await Task.Delay(500);
                return device;
            }
            catch
            {
                await Task.Delay(300 * attempt);
            }
        }

        return null;
    }

    private static async Task<List<BleCandidate>> ScanBleDevicesAsync(TimeSpan duration)
    {
        var byAddress = new Dictionary<ulong, BleCandidate>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        watcher.Received += (_, args) =>
        {
            var name = string.IsNullOrWhiteSpace(args.Advertisement.LocalName)
                ? "Unknown"
                : args.Advertisement.LocalName;

            byAddress[args.BluetoothAddress] = new BleCandidate(args.BluetoothAddress, name, "scan");
        };

        watcher.Start();
        await Task.Delay(duration);
        watcher.Stop();
        await Task.Delay(300);

        return byAddress.Values
            .Where(c => c.Name.Contains("click", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Address)
            .ToList();
    }

    private static async Task<List<BleCandidate>> GetWindowsBleDevicesAsync()
    {
        var requestedProperties = new[]
        {
            "System.Devices.Aep.DeviceAddress",
            "System.Devices.Aep.IsConnected"
        };

        var selectors = new[]
        {
            BluetoothLEDevice.GetDeviceSelector(),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)
        };

        var results = new List<BleCandidate>();
        foreach (var selector in selectors)
        {
            DeviceInformationCollection devices;
            try
            {
                devices = await DeviceInformation.FindAllAsync(selector, requestedProperties);
            }
            catch
            {
                continue;
            }

            foreach (var device in devices)
            {
                if (!TryParseDeviceAddress(device.Properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var rawAddress) ? rawAddress : null, out var address))
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(device.Name) ? "Unknown" : device.Name;
                results.Add(new BleCandidate(address, name, "windows", device.Id));
            }
        }

        return results
            .Where(c => c.Name.Contains("click", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Address)
            .Select(MergeCandidateGroup)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Address)
            .ToList();
    }

    private static async Task<(GattCharacteristic? control, GattCharacteristic? notify)> FindCharacteristicsAsync(BluetoothLEDevice device)
    {
        // First try known Zwift UUIDs.
        var knownServiceResult = await QueryWithRetryAsync(
            mode => device.GetGattServicesForUuidAsync(ServiceUuid, mode),
            result => result.Status,
            result => result.Services.Count,
            $"{device.Name}: bekende service ophalen");

        if (knownServiceResult != null && knownServiceResult.Status == GattCommunicationStatus.Success && knownServiceResult.Services.Count > 0)
        {
            var knownService = knownServiceResult.Services[0];
            var chars = await QueryWithRetryAsync(
                mode => knownService.GetCharacteristicsAsync(mode),
                result => result.Status,
                result => result.Characteristics.Count,
                $"{device.Name}: bekende characteristics ophalen");

            if (chars != null && chars.Status == GattCommunicationStatus.Success)
            {
                var knownControl = chars.Characteristics.FirstOrDefault(c => c.Uuid == ControlUuid);
                var knownNotify = chars.Characteristics.FirstOrDefault(c => c.Uuid == NotifyUuid);
                if (knownControl != null && knownNotify != null)
                {
                    Console.WriteLine("Bekende Zwift UUIDs gevonden.");
                    return (knownControl, knownNotify);
                }
            }
        }

        // Fallback: detect a service that exposes one write and one notify characteristic.
        var allServices = await QueryWithRetryAsync(
            mode => device.GetGattServicesAsync(mode),
            result => result.Status,
            result => result.Services.Count,
            $"{device.Name}: alle services ophalen");

        if (allServices == null || allServices.Status != GattCommunicationStatus.Success)
        {
            var status = allServices?.Status.ToString() ?? "Unknown";
            Console.WriteLine($"Services ophalen mislukt: {status}");
            Console.WriteLine("Controleer of de controller wakker is en niet al door Zwift/MyWhoosh/Companion is verbonden.");
            return (null, null);
        }

        Console.WriteLine("Bekende UUID niet gevonden, probeer auto-detect.");
        foreach (var service in allServices.Services)
        {
            var charsResult = await QueryWithRetryAsync(
                mode => service.GetCharacteristicsAsync(mode),
                result => result.Status,
                result => result.Characteristics.Count,
                $"{device.Name}: characteristics voor service {service.Uuid} ophalen");

            if (charsResult == null || charsResult.Status != GattCommunicationStatus.Success)
            {
                continue;
            }

            var writeChar = charsResult.Characteristics.FirstOrDefault(c =>
                c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                || c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write));

            var notifyChar = charsResult.Characteristics.FirstOrDefault(c =>
                c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify));

            if (writeChar != null && notifyChar != null)
            {
                Console.WriteLine($"Auto-detect service: {service.Uuid}");
                Console.WriteLine($"  Write char: {writeChar.Uuid}");
                Console.WriteLine($"  Notify char: {notifyChar.Uuid}");
                return (writeChar, notifyChar);
            }
        }

        return (null, null);
    }

    private static async Task<T?> QueryWithRetryAsync<T>(
        Func<BluetoothCacheMode, IAsyncOperation<T>> operation,
        Func<T, GattCommunicationStatus> getStatus,
        Func<T, int> getCount,
        string description)
        where T : class
    {
        T? lastResult = null;

        foreach (var cacheMode in new[] { BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached })
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var result = await operation(cacheMode);
                    lastResult = result;

                    if (getStatus(result) == GattCommunicationStatus.Success)
                    {
                        if (getCount(result) > 0 || cacheMode == BluetoothCacheMode.Uncached)
                        {
                            return result;
                        }
                    }
                    else if (attempt == 3)
                    {
                        Console.WriteLine($"{description} mislukt via {cacheMode}: {getStatus(result)}");
                    }
                }
                catch (Exception ex)
                {
                    if (attempt == 3)
                    {
                        Console.WriteLine($"{description} exception via {cacheMode}: {ex.Message}");
                    }
                }

                await Task.Delay(350 * attempt);
            }
        }

        return lastResult;
    }

    private static List<BleCandidate> GetPairedBluetoothDevicesFromRegistry()
    {
        var output = new List<BleCandidate>();
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");

            if (root == null)
            {
                return output;
            }

            foreach (var keyName in root.GetSubKeyNames())
            {
                using var subkey = root.OpenSubKey(keyName);
                var name = DecodeRegistryName(subkey?.GetValue("Name"));

                if (TryParseRegistryAddress(keyName, out var address))
                {
                    output.Add(new BleCandidate(address, name, "registry"));
                }
            }
        }
        catch
        {
            // Ignore registry failures and continue with scan results.
        }

        return output
            .GroupBy(c => c.Address)
            .Select(g => g.First())
            .ToList();
    }

    private static string DecodeRegistryName(object? raw)
    {
        if (raw is byte[] bytes)
        {
            var utf8 = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(utf8) && utf8 != "System.Byte[]")
            {
                return utf8;
            }

            var unicode = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(unicode))
            {
                return unicode;
            }
        }

        return raw?.ToString() ?? "Unknown";
    }

    private static bool TryParseRegistryAddress(string keyName, out ulong address)
    {
        return ulong.TryParse(keyName, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private static bool TryParseDeviceAddress(object? raw, out ulong address)
    {
        if (raw is string text)
        {
            var cleaned = text.Replace(":", string.Empty).Replace("-", string.Empty);
            return ulong.TryParse(cleaned, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
        }

        address = 0;
        return false;
    }

    private static BleCandidate MergeCandidateGroup(IEnumerable<BleCandidate> group)
    {
        var candidates = group.ToList();
        var best = candidates
            .OrderByDescending(GetCandidatePriority)
            .First();

        var preferredName = candidates
            .Select(c => c.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase))
            ?? best.Name;

        var deviceId = candidates
            .Select(c => c.DeviceId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

        return best with { Name = preferredName, DeviceId = deviceId };
    }

    private static int GetCandidatePriority(BleCandidate candidate)
    {
        var sourcePriority = candidate.Source switch
        {
            "windows" => 30,
            "scan" => 20,
            "registry" => 10,
            _ => 0
        };

        if (!string.IsNullOrWhiteSpace(candidate.DeviceId))
        {
            sourcePriority += 100;
        }

        if (!string.Equals(candidate.Name, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            sourcePriority += 5;
        }

        return sourcePriority;
    }

    private static string FormatAddress(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static async Task SendRideOn(ControllerConnection connection)
    {
        try
        {
            var writer = new DataWriter();
            writer.WriteString("RideOn");
            await connection.ControlChar.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SendRideOn fout voor {connection.Label}: {ex.Message}");
        }
    }

    private static void OnButtonChanged(ControllerConnection connection, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var data = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(data);

            if (data.Length == 0)
            {
                return;
            }

            var messageType = data[0];
            if (messageType == EmptyMessageType || messageType == BatteryLevelType)
            {
                return;
            }

            if (messageType == RideNotificationMessageType)
            {
                HandleRideStyleNotification(connection, data.AsSpan(1));
                return;
            }

            if (messageType != ClickNotificationMessageType)
            {
                Console.WriteLine($"{connection.Label}: onbekend bericht type=0x{messageType:X2}");
                return;
            }

            var pressed = ParseClickPressedMask(data.AsSpan(1));
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
                ExecuteAction(connection.Mapping.Down);
            }
            else if ((pressed & 0x02) != 0)
            {
                Console.WriteLine($"{connection.Label}: Plus (+)");
                ExecuteAction(connection.Mapping.Up);
            }
            else if ((pressed & 0x04) != 0)
            {
                Console.WriteLine($"{connection.Label}: Shift Up");
                ExecuteAction(connection.Mapping.ShiftUp);
            }
            else if ((pressed & 0x08) != 0)
            {
                Console.WriteLine($"{connection.Label}: Shift Down");
                ExecuteAction(connection.Mapping.ShiftDown);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Knop verwerking fout: {ex.Message}");
        }
    }

    private static void HandleRideStyleNotification(ControllerConnection connection, ReadOnlySpan<byte> message)
    {
        var index = 0;
        uint buttonMap = uint.MaxValue;

        while (index < message.Length)
        {
            var tag = message[index++];
            var fieldNumber = tag >> 3;
            var wireType = tag & 0x07;
            if (wireType != 0)
            {
                return;
            }

            var value = ReadVarint(message, ref index);
            if (fieldNumber == 1)
            {
                buttonMap = unchecked((uint)value);
                break;
            }
        }

        // Active-low bitmask: 1 = released, 0 = pressed.
        var pressedNow = ~buttonMap;
        var newPressed = pressedNow & ~connection.LastRidePressedMask;
        connection.LastRidePressedMask = pressedNow;

        if (newPressed == 0)
        {
            return;
        }

        var firstChangedBit = newPressed & (uint)-(int)newPressed;
        if (connection.RideDownMask == 0)
        {
            connection.RideDownMask = firstChangedBit;
            Console.WriteLine($"{connection.Label}: 0x23 map ingesteld -> Down bit 0x{connection.RideDownMask:X}");
        }
        else if (connection.RideUpMask == 0 && firstChangedBit != connection.RideDownMask)
        {
            connection.RideUpMask = firstChangedBit;
            Console.WriteLine($"{connection.Label}: 0x23 map ingesteld -> Up bit 0x{connection.RideUpMask:X}");
        }
        else if (connection.RideLeftMask == 0 &&
                 firstChangedBit != connection.RideDownMask &&
                 firstChangedBit != connection.RideUpMask)
        {
            connection.RideLeftMask = firstChangedBit;
            Console.WriteLine($"{connection.Label}: 0x23 map ingesteld -> Left bit 0x{connection.RideLeftMask:X}");
        }
        else if (connection.RideRightMask == 0 &&
                 firstChangedBit != connection.RideDownMask &&
                 firstChangedBit != connection.RideUpMask &&
                 firstChangedBit != connection.RideLeftMask)
        {
            connection.RideRightMask = firstChangedBit;
            Console.WriteLine($"{connection.Label}: 0x23 map ingesteld -> Right bit 0x{connection.RideRightMask:X}");
        }

        if (connection.RideDownMask != 0 && (newPressed & connection.RideDownMask) != 0)
        {
            Console.WriteLine($"{connection.Label}: Minus (-) (0x23)");
            ExecuteAction(connection.Mapping.Down);
        }

        if (connection.RideUpMask != 0 && (newPressed & connection.RideUpMask) != 0)
        {
            Console.WriteLine($"{connection.Label}: Plus (+) (0x23)");
            ExecuteAction(connection.Mapping.Up);
        }

        if (connection.RideLeftMask != 0 && (newPressed & connection.RideLeftMask) != 0)
        {
            Console.WriteLine($"{connection.Label}: Left (0x23)");
            ExecuteAction(connection.Mapping.ShiftUp);
        }

        if (connection.RideRightMask != 0 && (newPressed & connection.RideRightMask) != 0)
        {
            Console.WriteLine($"{connection.Label}: Right (0x23)");
            ExecuteAction(connection.Mapping.ShiftDown);
        }
    }

    private static byte ParseClickPressedMask(ReadOnlySpan<byte> message)
    {
        var pressed = (byte)0;
        var index = 0;
        while (index < message.Length)
        {
            var tag = message[index++];
            var fieldNumber = tag >> 3;
            var wireType = tag & 0x07;
            if (wireType != 0)
            {
                break;
            }

            var value = ReadVarint(message, ref index);
            switch (fieldNumber)
            {
                case 1 when value == 0:
                    pressed |= 0x02;
                    break;
                case 2 when value == 0:
                    pressed |= 0x01;
                    break;
            }
        }

        return pressed;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int index)
    {
        ulong value = 0;
        var shift = 0;
        while (index < data.Length)
        {
            var current = data[index++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
            if (shift >= 64)
            {
                break;
            }
        }

        return value;
    }

    private static void PrintMappings()
    {
        Console.WriteLine($"{Controller1Mapping.Label}: Minus(-)={Controller1Mapping.Down}, Plus(+)={Controller1Mapping.Up}, ShiftUp={Controller1Mapping.ShiftUp}, ShiftDown={Controller1Mapping.ShiftDown}");
        Console.WriteLine($"{Controller2Mapping.Label}: Minus(-)={Controller2Mapping.Down}, Plus(+)={Controller2Mapping.Up}, ShiftUp={Controller2Mapping.ShiftUp}, ShiftDown={Controller2Mapping.ShiftDown}");
    }

    private static void ExecuteAction(ButtonAction action)
    {
        switch (action)
        {
            case ButtonAction.None:
                return;
            case ButtonAction.KeyLeftBracket:
                Sim.Keyboard.KeyPress(VirtualKeyCode.OEM_4);
                return;
            case ButtonAction.KeyRightBracket:
                Sim.Keyboard.KeyPress(VirtualKeyCode.OEM_6);
                return;
            case ButtonAction.KeyI:
                Sim.Keyboard.KeyPress(VirtualKeyCode.VK_I);
                return;
            case ButtonAction.KeyK:
                Sim.Keyboard.KeyPress(VirtualKeyCode.VK_K);
                return;
            case ButtonAction.KeyLeftArrow:
                Sim.Keyboard.KeyPress(VirtualKeyCode.LEFT);
                return;
            case ButtonAction.KeyRightArrow:
                Sim.Keyboard.KeyPress(VirtualKeyCode.RIGHT);
                return;
            case ButtonAction.SpotifyPlayPause:
                Sim.Keyboard.KeyPress(VirtualKeyCode.MEDIA_PLAY_PAUSE);
                return;
            case ButtonAction.SpotifyNextTrack:
                Sim.Keyboard.KeyPress(VirtualKeyCode.MEDIA_NEXT_TRACK);
                return;
            case ButtonAction.SpotifyPreviousTrack:
                Sim.Keyboard.KeyPress(VirtualKeyCode.MEDIA_PREV_TRACK);
                return;
            case ButtonAction.SpotifyVolumeUp:
                Sim.Keyboard.KeyPress(VirtualKeyCode.VOLUME_UP);
                return;
            case ButtonAction.SpotifyVolumeDown:
                Sim.Keyboard.KeyPress(VirtualKeyCode.VOLUME_DOWN);
                return;
            default:
                return;
        }
    }

    private sealed record BleCandidate(ulong Address, string Name, string Source, string? DeviceId = null);
}