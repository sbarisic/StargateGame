param([string]$Bind = '0.0.0.0', [int]$Port = 7777, [int]$MaxPlayers = 8, [int]$Seed = 7319, [ValidateSet("temperate","desert","icy")][string]$Preset = "temperate", [switch]$DevPlanets,[string]$SaveDir="",[string]$Universe="default",[ValidateSet("auto","wilderness","ruins","village","city")][string]$Settlements="auto",[ValidateSet("auto","mega","seahill","desert","modern")][string]$SettlementStyle="auto")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $gameArgs = @("--bind",$Bind,"--port",$Port,"--max-players",$MaxPlayers,"--seed",$Seed,"--preset",$Preset)
    $gameArgs += @("--universe",$Universe,"--settlements",$Settlements,"--settlement-style",$SettlementStyle)
    if ($SaveDir) { $gameArgs += @("--save-dir",$SaveDir) }
    if ($DevPlanets) { $gameArgs += "--dev-planets" }
    dotnet run --no-launch-profile --project "$root/src/StargateGame.Server" -c Release -- @gameArgs
    exit $LASTEXITCODE
} finally { Pop-Location }
