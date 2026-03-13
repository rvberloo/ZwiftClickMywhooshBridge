namespace ZwiftClickController.Tests;

public class ClickProtocolParserTests
{
    [Fact]
    public void ParseClickPressedMask_PlusPressed_ReturnsPlusBit()
    {
        var message = new byte[] { 0x08, 0x00 };
        var pressed = ClickProtocolParser.ParseClickPressedMask(message);
        Assert.Equal(0x02, pressed);
    }

    [Fact]
    public void ParseClickPressedMask_MinusPressed_ReturnsMinusBit()
    {
        var message = new byte[] { 0x10, 0x00 };
        var pressed = ClickProtocolParser.ParseClickPressedMask(message);
        Assert.Equal(0x01, pressed);
    }

    [Fact]
    public void ParseClickPressedMask_BothPressed_ReturnsBothBits()
    {
        var message = new byte[] { 0x08, 0x00, 0x10, 0x00 };
        var pressed = ClickProtocolParser.ParseClickPressedMask(message);
        Assert.Equal(0x03, pressed);
    }

    [Fact]
    public void TryParseRidePressedMask_AllReleased_ReturnsNoPressedBits()
    {
        var message = new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F };
        var ok = ClickProtocolParser.TryParseRidePressedMask(message, out var pressed);
        Assert.True(ok);
        Assert.Equal(0u, pressed);
    }

    [Fact]
    public void TryParseRidePressedMask_Bit0Pressed_ReturnsBit0()
    {
        var message = new byte[] { 0x08, 0xFE, 0xFF, 0xFF, 0xFF, 0x0F };
        var ok = ClickProtocolParser.TryParseRidePressedMask(message, out var pressed);
        Assert.True(ok);
        Assert.Equal(1u, pressed);
    }

    [Fact]
    public void TryParseRidePressedMask_InvalidWireType_ReturnsFalse()
    {
        var message = new byte[] { 0x0A, 0x00 };
        var ok = ClickProtocolParser.TryParseRidePressedMask(message, out _);
        Assert.False(ok);
    }
}
