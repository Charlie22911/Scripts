# Keep-RDPSession-Active.ps1
#
# PURPOSE:
#   Keeps an RDP session from becoming idle by generating a tiny mouse
#   movement every 2 minutes using the built-in Windows SendInput API.
#
# HOW TO RUN:
#   1. Save this file as:
#        Keep-RDPSession-Active.ps1
#
#   2. Open PowerShell inside the Remote Desktop session.
#
#   3. Change to the folder containing this script.
#
#   4. Run:
#        powershell.exe -ExecutionPolicy Bypass -File .\Keep-RDPSession-Active.ps1
#
#   5. Leave the PowerShell window running.
#
#   6. Press Ctrl+C to stop the script.
#
# OPTIONAL:
#   To check whether Windows considers the session idle, run:
#        query user
#
#   Watch the "IDLE TIME" column. It should periodically return to
#   "." or "0" while this script is running.
#
# NOTES:
#   - The mouse moves 1 pixel right and immediately 1 pixel left.
#   - The pointer should finish at the same position where it started.
#   - No registry changes, Group Policy changes, or additional software
#     are required.
#   - This addresses idle-session timeouts. It will not bypass a fixed
#     maximum session-duration policy that disconnects or terminates a
#     session regardless of activity.
#
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MouseJiggler
{
    private const int INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public int type;
        public InputUnion U;
    }
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint nInputs,
        INPUT[] pInputs,
        int cbSize
    );
    public static bool Jiggle()
    {
        INPUT[] inputs = new INPUT[2];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].U.mi.dx = 1;
        inputs[0].U.mi.dy = 0;
        inputs[0].U.mi.mouseData = 0;
        inputs[0].U.mi.dwFlags = MOUSEEVENTF_MOVE;
        inputs[0].U.mi.time = 0;
        inputs[0].U.mi.dwExtraInfo = UIntPtr.Zero;
        inputs[1].type = INPUT_MOUSE;
        inputs[1].U.mi.dx = -1;
        inputs[1].U.mi.dy = 0;
        inputs[1].U.mi.mouseData = 0;
        inputs[1].U.mi.dwFlags = MOUSEEVENTF_MOVE;
        inputs[1].U.mi.time = 0;
        inputs[1].U.mi.dwExtraInfo = UIntPtr.Zero;
        uint sent = SendInput(
            (uint)inputs.Length,
            inputs,
            Marshal.SizeOf(typeof(INPUT))
        );
        return sent == inputs.Length;
    }
}
"@
$IntervalSeconds = 120
Write-Host "RDP mouse jiggler running."
Write-Host "Mouse will move 1 pixel and back every $IntervalSeconds seconds."
Write-Host "Press Ctrl+C to stop."
while ($true) {
    Start-Sleep -Seconds $IntervalSeconds
    if (-not [MouseJiggler]::Jiggle()) {
        Write-Warning "SendInput failed."
    }
}
