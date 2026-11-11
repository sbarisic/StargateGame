param([string]$Server = '', [int]$Port = 7777, [string]$Name = 'Explorer', [ValidateRange(0,300)][int]$SmokeSeconds = 0, [string]$CaptureDirectory = '', [switch]$SmokePeer, [ValidateSet('performance','low','high')][string]$Graphics = 'performance', [ValidateSet('on','off')][string]$VSync = 'on')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$clientArgs = @('--port', "$Port", '--name', $Name, '--graphics', $Graphics, '--vsync', $VSync)
if ($Server) { $clientArgs += @('--host', $Server) }
if ($SmokeSeconds) { $clientArgs += @('--smoke-seconds', "$SmokeSeconds") }
if ($CaptureDirectory) { $clientArgs += @('--capture-dir', $CaptureDirectory) }
if ($SmokePeer) { $clientArgs += '--smoke-peer' }
Push-Location $root
try {
    dotnet run --project "$root/src/StargateGame.Client" -c Release -- @clientArgs
    exit $LASTEXITCODE
} finally { Pop-Location }
