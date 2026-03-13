public static class ClickProtocolParser
{
    public const byte ClickNotificationMessageType = 0x37;
    public const byte RideNotificationMessageType = 0x23;
    public const byte EmptyMessageType = 0x15;
    public const byte BatteryLevelType = 0x19;

    public static byte ParseClickPressedMask(ReadOnlySpan<byte> message)
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

    public static bool TryParseRidePressedMask(ReadOnlySpan<byte> message, out uint pressedNow)
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
                pressedNow = 0;
                return false;
            }

            var value = ReadVarint(message, ref index);
            if (fieldNumber == 1)
            {
                buttonMap = unchecked((uint)value);
                break;
            }
        }

        // Active-low bitmask: 1 = released, 0 = pressed.
        pressedNow = ~buttonMap;
        return true;
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
}
