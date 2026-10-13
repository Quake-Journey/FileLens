param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$assetDirectory = Join-Path $projectDirectory 'Assets'
[IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
Add-Type -AssemblyName System.Drawing
$bitmap = [Drawing.Bitmap]::new(256, 256)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::FromArgb(11, 16, 26))
$accent = [Drawing.Pen]::new([Drawing.Color]::FromArgb(114, 228, 204), 13)
$accent.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
$accent.StartCap = $accent.EndCap = [Drawing.Drawing2D.LineCap]::Round
$paper = [Drawing.Point[]]@([Drawing.Point]::new(150, 42), [Drawing.Point]::new(66, 42), [Drawing.Point]::new(66, 207), [Drawing.Point]::new(192, 207), [Drawing.Point]::new(192, 86), [Drawing.Point]::new(150, 42))
$graphics.DrawLines($accent, $paper)
$graphics.DrawLines($accent, [Drawing.Point[]]@([Drawing.Point]::new(146, 44), [Drawing.Point]::new(146, 91), [Drawing.Point]::new(190, 91)))
$lensFill = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(11, 16, 26))
$graphics.FillEllipse($lensFill, 126, 125, 72, 72)
$graphics.DrawEllipse($accent, 126, 125, 72, 72)
$graphics.DrawLine($accent, 188, 187, 225, 224)
$pngStream = [IO.MemoryStream]::new()
$bitmap.Save($pngStream, [Drawing.Imaging.ImageFormat]::Png)
$png = $pngStream.ToArray()
$iconStream = [IO.File]::Create((Join-Path $assetDirectory 'FileLens.ico'))
$writer = [IO.BinaryWriter]::new($iconStream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$png.Length)
$writer.Write([uint32]22)
$writer.Write($png)
$writer.Dispose()
$pngStream.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
$accent.Dispose()
$lensFill.Dispose()
$releaseDirectory = Join-Path $projectDirectory 'release'
& dotnet publish (Join-Path $projectDirectory 'FileLens.csproj') -c Release -o $releaseDirectory --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$executable = Join-Path $releaseDirectory 'FileLens.exe'
if ($Test) {
    $artifacts = Join-Path $projectDirectory 'artifacts'
    [IO.Directory]::CreateDirectory($artifacts) | Out-Null
    $results = Join-Path $artifacts 'test-results.json'
    $process = Start-Process -FilePath $executable -ArgumentList @('--self-test', ('"' + $results + '"'), ('"' + $artifacts + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw 'Self-test failed.' }
    $report = Get-Content -LiteralPath $results -Raw | ConvertFrom-Json
    [pscustomobject]@{ Passed = $report.passed; Checks = $report.checks.Count; Failed = $report.failed; Architecture = $report.architecture } | Format-List
    & (Join-Path $projectDirectory 'tests\check32.ps1')
}
$publishedFile = Get-Item -LiteralPath $executable
$publishedHash = Get-FileHash -LiteralPath $executable -Algorithm SHA256
[pscustomobject]@{ Executable = $publishedFile.FullName; SizeMB = [Math]::Round($publishedFile.Length / 1MB, 2); SHA256 = $publishedHash.Hash } | Format-List
