# Turns the tank's timed snapshots into an MP4 time-lapse. Needs ffmpeg on the PATH
# (winget install Gyan.FFmpeg). Optional: the gallery zip already has a slideshow.
#
#   ./tools/make-timelapse.ps1                       # data/snapshots -> timelapse.mp4
#   ./tools/make-timelapse.ps1 -Snapshots path/to/unzipped/snapshots -SecondsPerFrame 0.5

param(
    [string]$Snapshots = "data/snapshots",
    [string]$Output = "timelapse.mp4",
    [double]$SecondsPerFrame = 0.4
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    Write-Error "ffmpeg isn't installed. Install it with: winget install Gyan.FFmpeg"
}

$frames = Get-ChildItem -Path $Snapshots -Filter *.png | Sort-Object Name
if ($frames.Count -eq 0) {
    Write-Error "No snapshots found in $Snapshots"
}

# ffmpeg's concat format: one "file" line per frame, each followed by how long to show it.
$list = New-TemporaryFile
$lines = foreach ($frame in $frames) {
    "file '$($frame.FullName -replace "'", "'\''")'"
    "duration $SecondsPerFrame"
}
$lines += "file '$($frames[-1].FullName -replace "'", "'\''")'" # the last frame needs repeating
[IO.File]::WriteAllLines($list.FullName, $lines) # UTF-8 without BOM; ffmpeg rejects a BOM

ffmpeg -y -f concat -safe 0 -i $list.FullName -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p" -r 30 $Output
Remove-Item $list
Write-Host "Wrote $Output from $($frames.Count) snapshots."
