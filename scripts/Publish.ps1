param([ValidateSet('win-x64','linux-x64')][string]$ServerRuntime = 'win-x64', [switch]$Client,[string]$OutputDirectory='', [string]$BuildCache='', [switch]$ReuseCooked)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root 'artifacts/publish'}
if(-not $BuildCache){$BuildCache=Join-Path $root 'artifacts/package-build'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory);$BuildCache=[IO.Path]::GetFullPath($BuildCache)
Push-Location $root
try {
    $catalogueCommand=if(Test-Path -LiteralPath "$root/artifacts/galaxy/galaxy.bin"){'verify'}else{'build'}
    dotnet run --project "$root/tools/GalaxyCatalogue" -c Release --artifacts-path "$BuildCache" -- $catalogueCommand "$root/artifacts/galaxy/galaxy.bin"
    if ($LASTEXITCODE -ne 0) { throw 'Galaxy catalogue build failed.' }
    dotnet publish "$root/src/StargateGame.Server" -c Release -r $ServerRuntime --artifacts-path "$BuildCache" --self-contained false -o "$OutputDirectory/server-$ServerRuntime"
    if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
    New-Item -ItemType Directory -Force "$OutputDirectory/server-$ServerRuntime/galaxy" | Out-Null
    Copy-Item -LiteralPath "$root/artifacts/galaxy/galaxy.bin" -Destination "$OutputDirectory/server-$ServerRuntime/galaxy/galaxy.bin"
    if ($Client) {
        $cooked = Join-Path $BuildCache 'packs'
        if (-not $ReuseCooked) {
            python "$root/tools/build_asset_packs.py" --root $root --cache $BuildCache --release
            if ($LASTEXITCODE) { throw 'Category asset cook failed.' }
        }
        if (-not (Test-Path -LiteralPath "$cooked/catalog.json")) { throw 'Cooked category catalog missing.' }
        $candidate = [IO.Path]::GetFullPath("$OutputDirectory/client-candidate-$([Guid]::NewGuid().ToString('N'))")
        $destination = [IO.Path]::GetFullPath("$OutputDirectory/client-win-x64")
        $previous = [IO.Path]::GetFullPath("$OutputDirectory/client-previous-$(Get-Date -Format yyyyMMdd-HHmmss)")
        $allowedRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + [IO.Path]::DirectorySeparatorChar
        foreach ($path in @($candidate, $destination, $previous)) {
            if (-not $path.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid publication path.' }
            if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing publication through a junction.' }
        }
        dotnet publish "$root/src/StargateGame.Client" -c Release -r win-x64 --artifacts-path "$BuildCache" --self-contained false "-p:CookedAssetPacks=$cooked" -p:CookAssets=false -o $candidate
        if ($LASTEXITCODE -ne 0) { throw 'Client publish failed; previous release retained.' }
        # The local menu owns matching server/harness processes; no checkout paths are needed.
        foreach ($project in @('StargateGame.Server','StargateGame.Harness')) {
            dotnet publish "$root/src/$project" -c Release -r win-x64 --artifacts-path "$BuildCache" --self-contained false -o "$candidate/testing/$project"
            if ($LASTEXITCODE) { throw "Local testing runtime publish failed: $project" }
        }
        New-Item -ItemType Directory -Force "$candidate/testing/StargateGame.Server/galaxy" | Out-Null
        Copy-Item -LiteralPath "$root/artifacts/galaxy/galaxy.bin" -Destination "$candidate/testing/StargateGame.Server/galaxy/galaxy.bin"
        & dotnet "$candidate/StargateGame.Client.dll" --verify-assets "$candidate/assets"
        if ($LASTEXITCODE -ne 0) { throw 'Published asset verification failed; previous release retained.' }
        if (Test-Path -LiteralPath "$candidate/assets/dev-override") { throw 'Development overlay must never ship.' }
        Set-Content -LiteralPath "$candidate/assets/cooked.required" -Value 'SGA-CATALOG-1'
        if (Test-Path -LiteralPath $destination) { Move-Item -LiteralPath $destination -Destination $previous }
        try { Move-Item -LiteralPath $candidate -Destination $destination }
        catch {
            if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $destination)) { Move-Item -LiteralPath $previous -Destination $destination }
            throw
        }
    }
} finally { Pop-Location }
