$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$executable = Join-Path $projectDirectory 'release\FileLens.exe'
$artifacts = Join-Path $projectDirectory 'artifacts'
$fixtureDirectory = Join-Path $artifacts ('x86-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
$powershell32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
$holderScript = Join-Path $PSScriptRoot 'holder32.ps1'
$checks = [Collections.Generic.List[object]]::new()
foreach ($mode in @('file','module')) {
    $target = Join-Path $fixtureDirectory ('32-bit Тест с пробелами' + $(if ($mode -eq 'module') { '.dll' } else { '.txt' }))
    if ($mode -eq 'module') { [IO.File]::Copy((Join-Path $env:WINDIR 'SysWOW64\version.dll'), $target) }
    else { [IO.File]::WriteAllText($target, 'FileLens x86 test') }
    $ready = Join-Path $fixtureDirectory ($mode + '.ready')
    $release = Join-Path $fixtureDirectory ($mode + '.release')
    $resultFile = Join-Path $fixtureDirectory ($mode + '-result.json')
    $holderArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $holderScript + '"'), '-Target', ('"' + $target + '"'), '-Ready', ('"' + $ready + '"'), '-Release', ('"' + $release + '"'), '-Mode', $mode)
    $holder = Start-Process -FilePath $powershell32 -ArgumentList $holderArgs -WindowStyle Hidden -PassThru
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not [IO.File]::Exists($ready)) {
            if ($holder.HasExited -or $timer.Elapsed.TotalSeconds -gt 25) { throw 'x86 fixture did not start.' }
            Start-Sleep -Milliseconds 100
        }
        $scan = Start-Process -FilePath $executable -ArgumentList @('--scan-json', ('"' + $target + '"'), ('"' + $resultFile + '"')) -WindowStyle Hidden -Wait -PassThru
        if ($scan.ExitCode -ne 0) { throw 'x86 scan failed.' }
        $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
        $owner = @($result.Result.Processes | Where-Object Pid -EQ $holder.Id)
        $passed = $owner.Count -eq 1
        if ($mode -eq 'module') { $passed = $passed -and $owner[0].LoadedModule }
        else { $passed = $passed -and $result.Result.DeleteAccessError -eq 32 }
        if ($mode -eq 'module' -and $result.Result.DeleteAccessError -eq 0) { $passed = $passed -and $result.Result.StatusTitle -eq 'Файл загружен в процесс' }
        $checks.Add([pscustomobject]@{ mode = $mode; passed = [bool]$passed; pid = $holder.Id; processes = $result.Result.Processes; deleteAccessError = $result.Result.DeleteAccessError })
        if (-not $passed) { throw ('x86 ' + $mode + ' detection failed.') }
    } finally {
        [IO.File]::WriteAllText($release, 'release')
        if (-not $holder.WaitForExit(10000)) { $holder.Kill(); $holder.WaitForExit() }
        $holder.Dispose()
    }
}
$checks | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $artifacts 'x86-test-results.json') -Encoding utf8
$checks | Select-Object mode,passed,pid,deleteAccessError
