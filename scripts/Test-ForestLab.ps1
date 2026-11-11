# Visual family alias; all process ownership and readiness live in Test.ps1.
param([Alias('Preset')][string[]]$Presets=@('temperate'), [Alias('View')][string[]]$Views=@(), [double[]]$Hours=@(12),
    [Alias('Graphics')][string]$Quality='performance', [string]$Weather='off', [string]$Grass='on',
    [string]$GroundMist='on', [string]$Clouds='on', [string]$Volumetrics='on', [string]$Reflections='on',
    [int]$Seconds=3, [Alias('Movie')][switch]$Motion, [switch]$FullMatrix, [Alias('Build')][string]$BuildRoot='',
    [switch]$NoBuild, [Alias('Output')][string]$OutputDirectory='', [hashtable]$Settings=@{})
& "$PSScriptRoot/Test-Visual.ps1" -Family 'forest' @PSBoundParameters
