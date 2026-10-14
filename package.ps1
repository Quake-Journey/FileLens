$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$executable = Join-Path $projectDirectory 'release\FileLens.exe'
$readme = Join-Path $projectDirectory 'README.md'
$archivePath = Join-Path $projectDirectory 'FileLens-Win64.zip'
$artifacts = Join-Path $projectDirectory 'artifacts'
$testResults = Get-Content -LiteralPath (Join-Path $artifacts 'test-results.json') -Raw | ConvertFrom-Json
$x86Results = @(Get-Content -LiteralPath (Join-Path $artifacts 'x86-test-results.json') -Raw | ConvertFrom-Json)
if (-not $testResults.passed -or @($x86Results | Where-Object { -not $_.passed }).Count -ne 0) { throw 'Tests must pass before packaging.' }
$reader = [IO.BinaryReader]::new([IO.File]::OpenRead($executable))
try {
    if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'Invalid DOS signature.' }
    $reader.BaseStream.Position = 0x3C
    $peOffset = $reader.ReadInt32()
    $reader.BaseStream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x00004550) { throw 'Invalid PE signature.' }
    $machine = $reader.ReadUInt16()
    if ($machine -ne 0x8664) { throw 'Executable is not AMD64.' }
} finally { $reader.Dispose() }
$hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
Compress-Archive -LiteralPath @($executable, $readme) -DestinationPath $archivePath -Force
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entry = $archive.GetEntry('FileLens.exe')
    if (-not $entry -or -not $archive.GetEntry('README.md')) { throw 'Archive contents incomplete.' }
    $stream = $entry.Open()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $archiveExeHash = [Convert]::ToHexString($hasher.ComputeHash($stream)) }
    finally { $hasher.Dispose(); $stream.Dispose() }
    if ($archiveExeHash -ne $hash) { throw 'Archive EXE hash mismatch.' }
} finally { $archive.Dispose() }
$manifest = [pscustomobject]@{
    Name = 'FileLens'
    Version = '1.1.0'
    Executable = $executable
    SHA256 = $hash
    Machine = 'AMD64 (0x8664)'
    Archive = $archivePath
    MainChecks = $testResults.checks.Count
    X86Checks = $x86Results.Count
    TestsPassed = $true
    PackagedAt = (Get-Date).ToString('o')
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifacts 'release-manifest.json') -Encoding utf8
$manifest | Format-List
