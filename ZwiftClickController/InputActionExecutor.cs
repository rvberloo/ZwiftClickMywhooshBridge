using WindowsInput;
using WindowsInput.Native;

public static class InputActionExecutor
{
    private static readonly InputSimulator Sim = new();

    public static void Execute(ButtonAction action)
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
            case ButtonAction.SpotifyPlayPauseShortcut:
                Sim.Keyboard.KeyPress(VirtualKeyCode.SPACE);
                return;
            case ButtonAction.SpotifyNextTrackShortcut:
                Sim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.RIGHT);
                return;
            case ButtonAction.SpotifyPreviousTrackShortcut:
                Sim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.LEFT);
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
}
