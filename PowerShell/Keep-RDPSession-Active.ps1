#requires -Version 5.1
<#
.SYNOPSIS
Sends periodic input inside the current, connected RDP session.
.DESCRIPTION
Run in PowerShell INSIDE the remote desktop, then leave it running:
    powershell.exe -NoProfile -File .\Keep-RDPSession-Active.ps1

The default pulse is an F15 press/release immediately, then every 30 seconds.
F15 does not type text or change lock keys, but an application can bind it to
a shortcut. If yours does, select -Mode Mouse for a tiny alternating movement.
The script never clicks, changes window focus, or modifies Windows settings.
It defers input while a key or mouse button is held, and pauses on a locked,
secure, or disconnected desktop. Press Ctrl+C or close PowerShell to stop.

Desktop idle feedback confirms only that GetLastInputInfo saw recent input;
it does not prove that a screensaver, RDS policy, or security agent accepts it.
The separate RDS idle counter is displayed when available. A policy that
ignores injected input, or a fixed session-duration limit, can still lock or
end the session. This script cannot unlock a session or keep a disconnected
session interactive. Keep the RDP connection open.

No installation, administrator rights, registry writes, services, scheduled
tasks, execution-policy changes, or Group Policy changes are made. Add-Type
uses the Windows/.NET components already present; Windows PowerShell can
create temporary compiler files. No runtime downloads are performed.

Read-only diagnostics (no input or power requests):
    .\Keep-RDPSession-Active.ps1 -Diagnose
One pulse with a structured result:
    .\Keep-RDPSession-Active.ps1 -Once
Alternative input:
    .\Keep-RDPSession-Active.ps1 -Mode Mouse

References:
https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput
https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getlastinputinfo
https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-systemparametersinfoa
#>
[CmdletBinding()]
param(
    [ValidateRange(5, 60)][int]$IntervalSeconds = 30,
    [ValidateSet('Keyboard', 'Mouse')][string]$Mode = 'Keyboard',
    [switch]$Diagnose,
    [switch]$Once,
    # Explicit opt-in for local testing; this cannot target a remote session.
    [switch]$AllowConsole
)

# ValidateSet accepts casing variants; pass a canonical value to native code.
$Mode = if ($Mode -ieq 'Mouse') { 'Mouse' } else { 'Keyboard' }
if ($env:OS -ne 'Windows_NT') { throw 'This script requires Windows.' }
if (-not ('RdpKeepAlive.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
namespace RdpKeepAlive {
    public static class Native {
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT { public uint type; public INPUTUNION data; }
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION {
            [FieldOffset(0)] public MOUSEINPUT mouse;
            [FieldOffset(0)] public KEYBDINPUT keyboard;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT {
            public int dx, dy;
            public uint mouseData, flags, time;
            public UIntPtr extraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT {
            public ushort vk, scan;
            public uint flags, time;
            public UIntPtr extraInfo;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint size, time; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WTSINFO {
            public int state;
            public uint sessionId, incomingBytes, outgoingBytes, incomingFrames,
                outgoingFrames, incomingCompressedBytes, outgoingCompressedBytes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string station;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string domain;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string user;
            public long connectTime, disconnectTime, lastInputTime, logonTime, currentTime;
        }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("kernel32.dll")] private static extern uint GetTickCount();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
        [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
        [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);

        private static IntPtr Query(int infoClass, int minimumBytes) {
            IntPtr buffer;
            int bytes;
            if (!WTSQuerySessionInformation(IntPtr.Zero, Process.GetCurrentProcess().SessionId,
                    infoClass, out buffer, out bytes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot query the current session.");
            if (buffer == IntPtr.Zero || bytes < minimumBytes) {
                if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
                throw new InvalidOperationException("Windows returned incomplete session information.");
            }
            return buffer;
        }
        public static int GetProtocol() {
            IntPtr buffer = Query(16, 2); // WTSClientProtocolType: 0 console, 2 RDP.
            try { return Marshal.ReadInt16(buffer); } finally { WTSFreeMemory(buffer); }
        }
        public static int GetState() {
            IntPtr buffer = Query(8, 4); // WTSConnectState: 0 active, 4 disconnected.
            try { return Marshal.ReadInt32(buffer); } finally { WTSFreeMemory(buffer); }
        }
        public static double? GetRdsIdleSeconds() {
            try {
                IntPtr buffer = Query(24, Marshal.SizeOf(typeof(WTSINFO)));
                try {
                    WTSINFO info = (WTSINFO)Marshal.PtrToStructure(buffer, typeof(WTSINFO));
                    if (info.lastInputTime <= 0 || info.currentTime < info.lastInputTime) return null;
                    return (info.currentTime - info.lastInputTime) / 10000000.0;
                } finally { WTSFreeMemory(buffer); }
            } catch (Win32Exception) { return null; }
              catch (InvalidOperationException) { return null; }
        }
        public static bool IsDefaultInputDesktop() {
            IntPtr desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS only.
            if (desktop == IntPtr.Zero) return false;
            try {
                StringBuilder name = new StringBuilder(256);
                int needed;
                return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out needed)
                    && String.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
            } finally { CloseDesktop(desktop); }
        }
        public static bool IsInputBusy() {
            for (int key = 1; key < 255; key++)
                if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
            return false;
        }
        public static bool GetInputResetBlocked() {
            int blocked;
            if (!SystemParametersInfo(0x1026, 0, out blocked, 0)) // SPI_GETBLOCKSENDINPUTRESETS, read only.
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read screensaver input-reset behavior.");
            return blocked != 0;
        }
        public static double GetIdleSeconds() {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.size = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // Unsigned subtraction also handles the 49.7-day tick-count wrap.
            return unchecked(GetTickCount() - info.time) / 1000.0;
        }
        public static void RefreshPower() {
            // Reset timers once; no ES_CONTINUOUS state to leave behind.
            if (SetThreadExecutionState(3) == 0)
                throw new InvalidOperationException("Windows rejected the temporary power-timer reset.");
        }
        public static bool Pulse(string mode, int direction) {
            if (GetState() != 0 || !IsDefaultInputDesktop() || IsInputBusy()) return false;
            INPUT[] inputs = new INPUT[mode == "Mouse" ? 1 : 2];
            if (mode == "Mouse") {
                inputs[0].data.mouse.dx = direction * 2;
                inputs[0].data.mouse.flags = 0x2001; // MOVE | MOVE_NOCOALESCE; no clicks.
            } else {
                inputs[0].type = inputs[1].type = 1; // INPUT_KEYBOARD.
                inputs[0].data.keyboard.vk = inputs[1].data.keyboard.vk = 0x7E; // F15.
                inputs[1].data.keyboard.flags = 2; // KEYEVENTF_KEYUP.
            }
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            int error = Marshal.GetLastWin32Error();
            if (sent != inputs.Length) {
                // If only key-down was accepted, make a best-effort release.
                if (mode != "Mouse" && sent == 1)
                    SendInput(1, new INPUT[] { inputs[1] }, Marshal.SizeOf(typeof(INPUT)));
                throw new Win32Exception(error, "SendInput accepted " + sent + "/" + inputs.Length
                    + " events. A secure desktop or higher-integrity foreground application can block input.");
            }
            return true;
        }
    }
}
'@ -ErrorAction Stop
}

function Get-RdpKeepAliveDiagnostics {
    $blocked = $null
    try { $blocked = [RdpKeepAlive.Native]::GetInputResetBlocked() } catch { Write-Warning $_.Exception.Message }
    [pscustomobject]@{
        Computer = $env:COMPUTERNAME
        SessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        Protocol = [RdpKeepAlive.Native]::GetProtocol()
        ConnectionState = [RdpKeepAlive.Native]::GetState()
        DefaultInputDesktop = [RdpKeepAlive.Native]::IsDefaultInputDesktop()
        DesktopIdleSeconds = [RdpKeepAlive.Native]::GetIdleSeconds()
        RdsIdleSeconds = [RdpKeepAlive.Native]::GetRdsIdleSeconds()
        ScreenSaverInputResetBlocked = $blocked
    }
    # Registry reads only. Absence does not establish effective policy.
    $checks = @(
        @{ Path = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'; Names = @('InactivityTimeoutSecs') },
        @{ Path = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'; Names = @('MaxIdleTime', 'MaxConnectionTime', 'MaxDisconnectionTime') },
        @{ Path = 'HKCU:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'; Names = @('MaxIdleTime', 'MaxConnectionTime', 'MaxDisconnectionTime') },
        @{ Path = 'HKCU:\SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop'; Names = @('ScreenSaveActive', 'ScreenSaveTimeOut', 'ScreenSaverIsSecure') },
        @{ Path = 'HKCU:\Control Panel\Desktop'; Names = @('ScreenSaveActive', 'ScreenSaveTimeOut', 'ScreenSaverIsSecure') }
    )
    foreach ($check in $checks) {
        foreach ($name in $check.Names) {
            try {
                $value = Get-ItemPropertyValue -LiteralPath $check.Path -Name $name -ErrorAction Stop
                [pscustomobject]@{ RegistryPath = $check.Path; Name = $name; Value = $value }
            } catch [System.Management.Automation.ItemNotFoundException] {
            } catch [System.Management.Automation.PSArgumentException] {
            } catch { Write-Warning "Cannot read $($check.Path)\${name}: $($_.Exception.Message)" }
        }
    }
}
if ($Diagnose) { Get-RdpKeepAliveDiagnostics; return }
if ([RdpKeepAlive.Native]::GetProtocol() -ne 2 -and -not $AllowConsole) {
    throw 'Run this script inside the RDP session on the remote server. -AllowConsole explicitly targets only the local desktop.'
}
$blocked = $null
try { $blocked = [RdpKeepAlive.Native]::GetInputResetBlocked() } catch { Write-Warning $_.Exception.Message }
if ($blocked) {
    Write-Warning 'Windows is configured to ignore SendInput for the screensaver timer. Desktop idle can reset while the screensaver still locks. No setting has been changed.'
}
Write-Host "RDP keep-alive: $Mode pulse every $IntervalSeconds seconds. Ctrl+C to stop."
Write-Host 'Keep the RDP connection open. Idle feedback is not proof that all lock policies accept input.'
$direction = 1
do {
    $status = 'Paused'
    $verified = $false
    $before = [RdpKeepAlive.Native]::GetIdleSeconds()
    if ([RdpKeepAlive.Native]::GetState() -eq 0 -and [RdpKeepAlive.Native]::IsDefaultInputDesktop()) {
        if ([RdpKeepAlive.Native]::IsInputBusy()) {
            $status = 'Busy'
        } elseif ([RdpKeepAlive.Native]::Pulse($Mode, $direction)) {
            $direction = -$direction
            [RdpKeepAlive.Native]::RefreshPower()
            # Let the desktop input thread process the events.
            Start-Sleep -Milliseconds 250
            $status = 'Sent'
        }
    }
    $after = [RdpKeepAlive.Native]::GetIdleSeconds()
    $rdsIdle = [RdpKeepAlive.Native]::GetRdsIdleSeconds()
    if ($status -eq 'Sent') {
        $verified = $after -lt 2
        if (-not $verified) { Write-Warning 'Input was submitted but desktop idle did not reset. This pulse has not been verified.' }
        if ($null -ne $rdsIdle -and $rdsIdle -gt [Math]::Max(60, 2 * $IntervalSeconds)) {
            Write-Warning 'The RDS idle counter is still high. Server-generated input may not be counting as RDP client activity; an RDS idle timeout may still apply.'
        }
    }
    if ($Once) {
        [pscustomobject]@{
            Mode = $Mode; Status = $status; InputVerified = $verified
            DesktopIdleBeforeSeconds = $before; DesktopIdleAfterSeconds = $after
            RdsIdleSeconds = $rdsIdle; ScreenSaverInputResetBlocked = $blocked
        }
        return
    }
    $rdsText = if ($null -eq $rdsIdle) { 'unavailable' } else { '{0:N1}s' -f $rdsIdle }
    Write-Host ('{0:HH:mm:ss}  {1,-6}  Desktop idle: {2:N1}s  RDS idle: {3}' -f (Get-Date), $status, $after, $rdsText)
    Start-Sleep -Seconds $IntervalSeconds
} while ($true)
