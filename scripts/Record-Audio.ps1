# Optional manual audiovisual review; automated dialing coverage is Test.ps1 run -Recipe audio.
param([string]$OutputDirectory='artifacts/audio-video', [string]$BuildRoot='', [switch]$NoBuild,
      [ValidateRange(1,120)][int]$Seconds=30)
$ErrorActionPreference='Stop'
if (!(Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw 'Optional video recording requires ffmpeg on PATH.' }
$output=[IO.Path]::GetFullPath($OutputDirectory); New-Item -ItemType Directory -Force $output | Out-Null
$session=& "$PSScriptRoot/Test.ps1" start -Scenario sandbox -BuildRoot $BuildRoot -NoBuild:$NoBuild -Json | ConvertFrom-Json
function Action([hashtable]$Data) {
    $request=Join-Path $output 'action.json'
    $Data | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $request
    & "$PSScriptRoot/Test.ps1" action -Session $session.session -Request $request | Out-Null
}
try {
    Action @{action='audio-record';data=@{name='mix.wav'}}
    $client=Get-Process -Id $session.manifest.clientPids[0]
    Write-Host "Recording $Seconds seconds of manual play in $($client.MainWindowTitle)."
    ffmpeg -v error -y -f gdigrab -framerate 30 -i "title=$($client.MainWindowTitle)" -t $Seconds -c:v libx264 -preset veryfast -crf 22 -pix_fmt yuv420p "$output/video.mp4"
    if($LASTEXITCODE){throw 'Window capture failed.'}
    Action @{action='audio-record';data=@{stop=$true}}
    $audio=Join-Path $session.artifacts 'client-1/mix.wav'
    ffmpeg -v error -y -i "$output/video.mp4" -i $audio -map 0:v -map 1:a -c:v copy -c:a aac -shortest "$output/gate-audio.mp4"
    if($LASTEXITCODE){throw 'Audio/video mux failed.'}
    'Manual review recording. Audio starts just before video; allow recorder-start latency when reviewing sync.' | Set-Content "$output/sync.txt"
} finally { & "$PSScriptRoot/Test.ps1" stop -Session $session.session | Out-Null }
