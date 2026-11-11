$ErrorActionPreference = 'Stop'
python (Join-Path $PSScriptRoot '../tools/export_collision.py')
if ($LASTEXITCODE -ne 0) { throw 'Collision export failed.' }
