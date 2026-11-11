#requires -Version 7.0
<#
.SYNOPSIS
Restore missing, Git-ignored runtime assets from an existing local checkout.
.EXAMPLE
./scripts/Restore-LocalAssets.ps1 -SourceRoot E:/Projects/StargateGame-Assets
.EXAMPLE
./scripts/Restore-LocalAssets.ps1 -SourceRoot E:/Projects/StargateGame-Assets -MetadataOnly
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string]$SourceRoot, [switch]$MetadataOnly)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$source=(Resolve-Path -LiteralPath $SourceRoot).ProviderPath
if($source.TrimEnd('\','/') -eq $repo.TrimEnd('\','/')) { Write-Host 'Assets already use this checkout.'; return }
$files=[Collections.Generic.List[string]]::new()
if($MetadataOnly) {
    foreach($project in Get-ChildItem -LiteralPath "$repo/src" -Filter '*.csproj' -Recurse -File) {
        [xml]$xml=Get-Content -LiteralPath $project.FullName -Raw
        foreach($resource in $xml.SelectNodes('//EmbeddedResource[@Include]')) {
            $full=[IO.Path]::GetFullPath((Join-Path $project.DirectoryName $resource.Include))
            $relative=[IO.Path]::GetRelativePath($repo,$full).Replace('\','/')
            if(!$relative.StartsWith('assets/')) { continue }
            if([Management.Automation.WildcardPattern]::ContainsWildcardCharacters($relative)) {
                foreach($file in Get-ChildItem -Path (Join-Path $source $relative) -File) {
                    $files.Add([IO.Path]::GetRelativePath($source,$file.FullName).Replace('\','/'))
                }
            } else { $files.Add($relative) }
        }
    }
} else {
    foreach($relative in @('assets','thirdparty/FishGfx/FishGfx/data','thirdparty/FishGfx/BMFont','thirdparty/FishGfx/thirdparty/FishUI/FishUI/data')) {
        $folder=Join-Path $source $relative
        if(Test-Path -LiteralPath $folder) {
            foreach($file in Get-ChildItem -LiteralPath $folder -File -Recurse) {
                $files.Add([IO.Path]::GetRelativePath($source,$file.FullName).Replace('\','/'))
            }
        }
    }
}
if(!$files.Count) { throw 'No source assets found.' }
# NUL-delimited input avoids PowerShell's CRLF pipeline becoming part of paths,
# and safely preserves spaces, Unicode, and Git-quoted filenames.
$info=[Diagnostics.ProcessStartInfo]::new('git')
$info.UseShellExecute=$false; $info.CreateNoWindow=$true
$info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true
$info.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
$info.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
foreach($arg in @('-C',$repo,'check-ignore','--stdin','-z')) { $info.ArgumentList.Add($arg) }
$process=[Diagnostics.Process]::Start($info)
try {
    $output=$process.StandardOutput.ReadToEndAsync()
    $process.StandardInput.Write(($files -join "`0")+"`0"); $process.StandardInput.Close()
    $process.WaitForExit()
    if($process.ExitCode -gt 1) { throw 'Cannot check repository ignore rules.' }
    $ignored=@($output.GetAwaiter().GetResult().Split([char]0,[StringSplitOptions]::RemoveEmptyEntries))
} finally { $process.Dispose() }
$copied=0; $existing=0
foreach($relative in $ignored) {
    $target=[IO.Path]::GetFullPath((Join-Path $repo $relative))
    if(!$target.StartsWith($repo.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid destination path.' }
    if(Test-Path -LiteralPath $target) { $existing++; continue }
    for($cursor=Split-Path $target -Parent; $cursor; $cursor=Split-Path $cursor -Parent) {
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Destination contains a link: $cursor" }
    }
    $original=Join-Path $source $relative
    if(!(Test-Path -LiteralPath $original -PathType Leaf)) { throw "Missing source asset: $original" }
    if($PSCmdlet.ShouldProcess($target,'Copy missing local asset')) {
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $original -Destination $target
        $copied++
    }
}
Write-Host "Copied $copied missing ignored assets; retained $existing existing files. Nothing added to Git."
