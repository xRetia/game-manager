// Gamepad input via the classic WinMM joystick API (works for Xbox pads through DirectInput).
// Navigation: dpad (POV hat) and left stick. Confirm: A (button 1) or X (button 3).

using System;
using System.Runtime.InteropServices;

namespace GameManager;

internal static class GamePad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct JOYINFOEX
    {
        public int dwSize;
        public int dwFlags;
        public int dwXpos;
        public int dwYpos;
        public int dwZpos;
        public int dwRpos;
        public int dwUpos;
        public int dwVpos;
        public int dwButtons;
        public int dwButtonNumber;
        public int dwPOV;
        public int dwReserved1;
        public int dwReserved2;
    }

    private const int JOY_RETURNX = 0x1;
    private const int JOY_RETURNY = 0x2;
    private const int JOY_RETURNPOV = 0x40;
    private const int JOY_RETURNBUTTONS = 0x80;

    private const int MaxPads = 4;
    private const int AxisCenter = 32768;
    private const int AxisDeadzone = 20000;

    // Xbox controller button mapping as seen by DirectInput: 1 = A, 2 = B, 3 = X, 4 = Y.
    private const int ButtonA = 1 << 0;
    private const int ButtonX = 1 << 2;

    [DllImport("winmm.dll")]
    private static extern int joyGetPosEx(int uJoyID, ref JOYINFOEX pji);

    /// <summary>
    /// Polls all connected pads. Any pad's dpad or left stick sets the direction;
    /// A or X on any pad confirms. Returns true when at least one pad responded.
    /// </summary>
    public static bool Poll(out int dx, out int dy, out bool confirm)
    {
        dx = 0;
        dy = 0;
        confirm = false;
        bool anyPad = false;

        for (int id = 0; id < MaxPads; id++)
        {
            var info = new JOYINFOEX
            {
                dwSize = Marshal.SizeOf<JOYINFOEX>(),
                dwFlags = JOY_RETURNX | JOY_RETURNY | JOY_RETURNPOV | JOY_RETURNBUTTONS
            };
            if (joyGetPosEx(id, ref info) != 0) continue;
            anyPad = true;

            // Dpad: POV in hundredths of a degree; -1 means neutral.
            if (info.dwPOV >= 0)
            {
                double deg = info.dwPOV / 100.0;
                if (deg < 45 || deg > 315) dy = -1;
                else if (deg > 135 && deg < 225) dy = 1;
                if (deg > 45 && deg < 135) dx = 1;
                else if (deg > 225 && deg < 315) dx = -1;
            }

            // Left stick axes (0..65535, centered at 32768).
            if (info.dwXpos > AxisCenter + AxisDeadzone) dx = 1;
            else if (info.dwXpos < AxisCenter - AxisDeadzone) dx = -1;
            if (info.dwYpos > AxisCenter + AxisDeadzone) dy = 1;
            else if (info.dwYpos < AxisCenter - AxisDeadzone) dy = -1;

            if ((info.dwButtons & (ButtonA | ButtonX)) != 0) confirm = true;
        }

        return anyPad;
    }
}