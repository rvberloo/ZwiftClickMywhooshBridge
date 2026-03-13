using System.Globalization;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;

public sealed class BleDiscoveryService
{
    public async Task<List<BleCandidate>> ScanBleDevicesAsync(TimeSpan duration)
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

    public List<BleCandidate> GetPairedBluetoothDevicesFromRegistry()
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

    public async Task<List<BleCandidate>> GetWindowsBleDevicesAsync()
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

    public List<BleCandidate> MergeCandidates(
        IEnumerable<BleCandidate> scanCandidates,
        IEnumerable<BleCandidate> windowsCandidates,
        IEnumerable<BleCandidate> registryCandidates)
    {
        return scanCandidates
            .Concat(windowsCandidates)
            .Concat(registryCandidates)
            .GroupBy(c => c.Address)
            .Select(MergeCandidateGroup)
            .ToList();
    }

    public static string FormatAddress(ulong address)
    {
        var hex = address.ToString("X12");
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
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
}
