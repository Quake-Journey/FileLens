param(
    [Parameter(Mandatory)][string]$Target,
    [Parameter(Mandatory)][string]$Ready,
    [Parameter(Mandatory)][string]$Release,
    [ValidateSet('file','module')][string]$Mode = 'file'
)
$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 4) { throw 'This fixture must run in 32-bit Windows PowerShell.' }
$stream = $null
$library = [IntPtr]::Zero
try {
    if ($Mode -eq 'file') {
        $stream = [IO.FileStream]::new($Target, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    } else {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FileLensFixture32 {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll")]
    public static extern bool FreeLibrary(IntPtr module);
}
'@
        $library = [FileLensFixture32]::LoadLibrary($Target)
        if ($library -eq [IntPtr]::Zero) { throw 'LoadLibrary failed.' }
    }
    [IO.File]::WriteAllText($Ready, $PID.ToString())
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.File]::Exists($Release) -and $timer.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 100 }
} finally {
    if ($stream) { $stream.Dispose() }
    if ($library -ne [IntPtr]::Zero) { [FileLensFixture32]::FreeLibrary($library) | Out-Null }
}
