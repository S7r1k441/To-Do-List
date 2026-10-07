# Builds TaskBoard.exe and places it one folder up (next to this "source" folder).
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Join-Path (Split-Path $src -Parent) 'TaskBoard.exe'

if (-not (Test-Path (Join-Path $src 'app.ico'))) { & (Join-Path $src 'make-icon.ps1') }

# Wallpaper Engine library thumbnail, rendered from the wallpaper's demo mode.
$preview = Join-Path $src 'web\wallpaper-preview.jpg'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($edge -and -not (Test-Path $preview)) {
    $tmp = Join-Path $env:TEMP 'taskboard-preview'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $url = 'file:///' + ((Join-Path $src 'web\wallpaper.html') -replace '\\', '/') + '?demo'
    Start-Process $edge -Wait -WindowStyle Hidden -ArgumentList @('--headless=new', '--disable-gpu', '--hide-scrollbars', "--user-data-dir=`"$tmp\profile`"",
        '--window-size=1600,900', "--screenshot=`"$tmp\shot.png`"", '--virtual-time-budget=3000', "`"$url`"")
    if (Test-Path "$tmp\shot.png") {
        Add-Type -AssemblyName System.Drawing
        $img = [System.Drawing.Image]::FromFile("$tmp\shot.png")
        $out = New-Object System.Drawing.Bitmap 640, 360
        $g = [System.Drawing.Graphics]::FromImage($out)
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.DrawImage($img, 0, 0, 640, 360)
        $out.Save($preview, [System.Drawing.Imaging.ImageFormat]::Jpeg)
        $g.Dispose(); $out.Dispose(); $img.Dispose()
    }
}

dotnet publish (Join-Path $src 'TaskBoard.csproj') -c Release -o (Join-Path $src 'publish') --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$running = Get-Process TaskBoard -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Closing running TaskBoard..."
    $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Seconds 2
    Get-Process TaskBoard -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

Copy-Item (Join-Path $src 'publish\TaskBoard.exe') $dest -Force
Write-Host "Built $dest"
