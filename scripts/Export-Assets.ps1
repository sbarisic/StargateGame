param(
    [string]$SourceBlend = 'D:\Games\Stargate Network\extraction\assembled\Stargate_and_DHD.blend',
    [string]$Blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& $Blender --background $SourceBlend --python "$root/tools/export_assets.py" -- --output "$root/assets/models"
if ($LASTEXITCODE -ne 0) { throw 'Asset export failed.' }
