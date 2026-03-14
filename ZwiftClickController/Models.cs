using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

public enum ButtonAction
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
    SpotifyVolumeDown,
    CtrlLeft,
    CtrlRight,
    SpaceBar
}

public sealed record ControllerMapping(
    string Label,
    ButtonAction Minus,
    ButtonAction Plus,
    ButtonAction ShiftUp,
    ButtonAction ShiftDown);

public sealed class ControllerConnection
{
    public required string Label { get; init; }
    public required ulong Address { get; init; }
    public required BluetoothLEDevice Device { get; init; }
    public required GattCharacteristic ControlChar { get; init; }
    public required GattCharacteristic NotifyChar { get; init; }
    public required ControllerMapping Mapping { get; init; }
    public byte LastPressedMask { get; set; }
    public uint LastRidePressedMask { get; set; }
    public uint RideMinusMask { get; set; }
    public uint RidePlusMask { get; set; }
    public uint RideLeftMask { get; set; }
    public uint RideRightMask { get; set; }
    public DateTime LastNotificationUtc { get; set; }
    public DateTime LastButtonNotificationUtc { get; set; }
    public bool HasSawButtonNotification { get; set; }
    public int KeepAliveFailureCount { get; set; }
}

public sealed record BleCandidate(ulong Address, string Name, string Source, string? DeviceId = null);
