using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

class Program
{
    private static readonly List<ControllerConnection> Connections = new();
    private static readonly BleDiscoveryService Discovery = new();
    private static readonly GattDiscoveryService Gatt = new();

    // Zwift Click terminology:
    // Minus = (-) button, Plus = (+) button.
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
        ButtonAction.SpotifyPreviousTrackShortcut,
        ButtonAction.SpotifyNextTrackShortcut,
        ButtonAction.SpotifyPlayPauseShortcut,
        ButtonAction.KeyRightArrow);

    static async Task Main()
    {
        try
        {
            Console.WriteLine("=== Zwift Click C# Controller ===");
            Console.WriteLine("Zoeken naar BLE apparaten...");
            PrintMappings();

            var scanCandidates = await Discovery.ScanBleDevicesAsync(TimeSpan.FromSeconds(8));
            Console.WriteLine($"Methode 1 (scan): {scanCandidates.Count} kandidaten");
            foreach (var c in scanCandidates)
            {
                if (c.Name.Contains("click", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  - {c.Name} [{BleDiscoveryService.FormatAddress(c.Address)}] ({c.Source})");
                }
            }

            var registryCandidates = Discovery.GetPairedBluetoothDevicesFromRegistry();
            Console.WriteLine($"Methode 2 (registry): {registryCandidates.Count} kandidaten");
            foreach (var c in registryCandidates)
            {
                Console.WriteLine($"  - {c.Name} [{BleDiscoveryService.FormatAddress(c.Address)}] ({c.Source})");
            }

            var deviceInfoCandidates = await Discovery.GetWindowsBleDevicesAsync();
            Console.WriteLine($"Methode 3 (windows devices): {deviceInfoCandidates.Count} kandidaten");
            foreach (var c in deviceInfoCandidates)
            {
                Console.WriteLine($"  - {c.Name} [{BleDiscoveryService.FormatAddress(c.Address)}] ({c.Source})");
            }

            var merged = Discovery.MergeCandidates(scanCandidates, deviceInfoCandidates, registryCandidates);
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
                Console.WriteLine($"Probeer verbinden: {candidate.Name} [{BleDiscoveryService.FormatAddress(candidate.Address)}]");
                var device = await Gatt.TryConnectAsync(candidate);
                if (device == null)
                {
                    continue;
                }

                Console.WriteLine($"Verbonden met: {device.Name} [{BleDiscoveryService.FormatAddress(candidate.Address)}]");

                var selectedChars = await Gatt.FindCharacteristicsAsync(device);
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
                        RideMinusMask = 0,
                        RidePlusMask = 0,
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
                    Console.WriteLine($"{connection.Label} actief op [{BleDiscoveryService.FormatAddress(candidate.Address)}]");
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
            if (messageType == ClickProtocolParser.EmptyMessageType || messageType == ClickProtocolParser.BatteryLevelType)
            {
                return;
            }

            if (messageType == ClickProtocolParser.RideNotificationMessageType)
            {
                HandleRideStyleNotification(connection, data.AsSpan(1));
                return;
            }

            if (messageType != ClickProtocolParser.ClickNotificationMessageType)
            {
                Console.WriteLine($"{connection.Label}: onbekend bericht type=0x{messageType:X2}");
                return;
            }

            var pressed = ClickProtocolParser.ParseClickPressedMask(data.AsSpan(1));
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
            Console.WriteLine($"Knop verwerking fout: {ex.Message}");
        }
    }

    private static void HandleRideStyleNotification(ControllerConnection connection, ReadOnlySpan<byte> message)
    {
        if (!ClickProtocolParser.TryParseRidePressedMask(message, out var pressedNow))
        {
            return;
        }

        var newPressed = pressedNow & ~connection.LastRidePressedMask;
        connection.LastRidePressedMask = pressedNow;

        if (newPressed == 0)
        {
            return;
        }

        var firstChangedBit = newPressed & (uint)-(int)newPressed;
        if (connection.RideMinusMask == 0)
        {
            connection.RideMinusMask = firstChangedBit;
            Console.WriteLine($"{connection.Label}: 0x23 map ingesteld -> Minus bit 0x{connection.RideMinusMask:X}");
        }
        else if (connection.RidePlusMask == 0 && firstChangedBit != connection.RideMinusMask)
        {
            connection.RidePlusMask = firstChangedBit;
            Console.WriteLine($"{connection.Label}: 0x23 map ingesteld -> Plus bit 0x{connection.RidePlusMask:X}");
        }
        else if (connection.RideLeftMask == 0 &&
                 firstChangedBit != connection.RideMinusMask &&
                 firstChangedBit != connection.RidePlusMask)
        {
            connection.RideLeftMask = firstChangedBit;
            Console.WriteLine($"{connection.Label}: 0x23 map ingesteld -> Left bit 0x{connection.RideLeftMask:X}");
        }
        else if (connection.RideRightMask == 0 &&
                 firstChangedBit != connection.RideMinusMask &&
                 firstChangedBit != connection.RidePlusMask &&
                 firstChangedBit != connection.RideLeftMask)
        {
            connection.RideRightMask = firstChangedBit;
            Console.WriteLine($"{connection.Label}: 0x23 map ingesteld -> Right bit 0x{connection.RideRightMask:X}");
        }

        if (connection.RideMinusMask != 0 && (newPressed & connection.RideMinusMask) != 0)
        {
            Console.WriteLine($"{connection.Label}: Minus (-) (0x23)");
            InputActionExecutor.Execute(connection.Mapping.Minus);
        }

        if (connection.RidePlusMask != 0 && (newPressed & connection.RidePlusMask) != 0)
        {
            Console.WriteLine($"{connection.Label}: Plus (+) (0x23)");
            InputActionExecutor.Execute(connection.Mapping.Plus);
        }

        if (connection.RideLeftMask != 0 && (newPressed & connection.RideLeftMask) != 0)
        {
            Console.WriteLine($"{connection.Label}: Left (0x23)");
            InputActionExecutor.Execute(connection.Mapping.ShiftUp);
        }

        if (connection.RideRightMask != 0 && (newPressed & connection.RideRightMask) != 0)
        {
            Console.WriteLine($"{connection.Label}: Right (0x23)");
            InputActionExecutor.Execute(connection.Mapping.ShiftDown);
        }
    }

    private static void PrintMappings()
    {
        Console.WriteLine($"{Controller1Mapping.Label}: Minus(-)={Controller1Mapping.Minus}, Plus(+)={Controller1Mapping.Plus}, ShiftUp={Controller1Mapping.ShiftUp}, ShiftDown={Controller1Mapping.ShiftDown}");
        Console.WriteLine($"{Controller2Mapping.Label}: Minus(-)={Controller2Mapping.Minus}, Plus(+)={Controller2Mapping.Plus}, ShiftUp={Controller2Mapping.ShiftUp}, ShiftDown={Controller2Mapping.ShiftDown}");
    }
}
