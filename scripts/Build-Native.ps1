$ErrorActionPreference = 'Stop'
python (Join-Path $PSScriptRoot '../tools/build_native.py')
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
