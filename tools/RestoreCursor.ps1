# Emergency restore of the default Windows cursors.
# Use this if WormsCursor.exe was force-killed and the cursor got stuck rotated.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Cur {
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint b, IntPtr c, uint d);
}
'@
[Cur]::SystemParametersInfo(0x0057, 0, [IntPtr]::Zero, 0) | Out-Null
Write-Host "Default cursors restored."
