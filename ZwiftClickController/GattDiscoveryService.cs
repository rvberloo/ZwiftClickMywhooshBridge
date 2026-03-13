using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;

public sealed class GattDiscoveryService
{
    private static readonly Guid ServiceUuid = new("00000001-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid NotifyUuid = new("00000002-19ca-4651-86e5-fa29dcdd09d1");
    private static readonly Guid ControlUuid = new("00000003-19ca-4651-86e5-fa29dcdd09d1");

    public async Task<BluetoothLEDevice?> TryConnectAsync(BleCandidate candidate)
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
                    Console.WriteLine($"Access denied for {candidate.Name}: {access}");
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

    public async Task<(GattCharacteristic? control, GattCharacteristic? notify)> FindCharacteristicsAsync(BluetoothLEDevice device)
    {
        var knownServiceResult = await QueryWithRetryAsync(
            mode => device.GetGattServicesForUuidAsync(ServiceUuid, mode),
            result => result.Status,
            result => result.Services.Count,
            $"{device.Name}: retrieving known service");

        if (knownServiceResult != null && knownServiceResult.Status == GattCommunicationStatus.Success && knownServiceResult.Services.Count > 0)
        {
            var knownService = knownServiceResult.Services[0];
            var chars = await QueryWithRetryAsync(
                mode => knownService.GetCharacteristicsAsync(mode),
                result => result.Status,
                result => result.Characteristics.Count,
                $"{device.Name}: retrieving known characteristics");

            if (chars != null && chars.Status == GattCommunicationStatus.Success)
            {
                var knownControl = chars.Characteristics.FirstOrDefault(c => c.Uuid == ControlUuid);
                var knownNotify = chars.Characteristics.FirstOrDefault(c => c.Uuid == NotifyUuid);
                if (knownControl != null && knownNotify != null)
                {
                    Console.WriteLine("Known Zwift UUIDs found.");
                    return (knownControl, knownNotify);
                }
            }
        }

        var allServices = await QueryWithRetryAsync(
            mode => device.GetGattServicesAsync(mode),
            result => result.Status,
            result => result.Services.Count,
            $"{device.Name}: retrieving all services");

        if (allServices == null || allServices.Status != GattCommunicationStatus.Success)
        {
            var status = allServices?.Status.ToString() ?? "Unknown";
            Console.WriteLine($"Failed to retrieve services: {status}");
            Console.WriteLine("Check that the controller is awake and not already connected by Zwift/MyWhoosh/Companion.");
            return (null, null);
        }

        Console.WriteLine("Known UUID not found, trying auto-detect.");
        foreach (var service in allServices.Services)
        {
            var charsResult = await QueryWithRetryAsync(
                mode => service.GetCharacteristicsAsync(mode),
                result => result.Status,
                result => result.Characteristics.Count,
                $"{device.Name}: retrieving characteristics for service {service.Uuid}");

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
                        Console.WriteLine($"{description} failed via {cacheMode}: {getStatus(result)}");
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
}
