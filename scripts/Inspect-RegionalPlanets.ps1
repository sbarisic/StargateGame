param([string]$Scene='mountain-meadow',[int]$Warmup=45,[int]$Seconds=5,[float]$Hour=11,
 [string]$ReviewRoot='artifacts/approved-terrain',
 [string]$BinRoot='artifacts/validation/approved-terrain-final/bin')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$review=Join-Path $root $ReviewRoot
$view=Get-Content "$review/views.json" -Raw|ConvertFrom-Json|Where-Object name -eq $Scene
if(!$view){throw 'Run the --regional focused checks first to write deterministic camera positions.'}
$out="$review/$Scene";New-Item -ItemType Directory -Force $out|Out-Null
$bin=Join-Path $root $BinRoot
$previousCache=$env:STARGATEGAME_TERRAIN_CACHE
$env:STARGATEGAME_TERRAIN_CACHE="$review/terrain-cache"
New-Item -ItemType Directory -Force "$review/terrain-cache/regional"|Out-Null
Get-ChildItem "$review/cache" -Filter '*.rgn' -ErrorAction SilentlyContinue|Copy-Item -Destination "$review/terrain-cache/regional"
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
$owned=@()
try{
 $s=Start-Process dotnet -WindowStyle Hidden -PassThru -WorkingDirectory $root -ArgumentList @("$bin/StargateGame.Server/release/StargateGame.Server.dll",'--bind','127.0.0.1','--port',"$port",'--seed',"$($view.seed)",'--preset',$view.preset,'--landscape',$view.mode,'--settlements','wilderness','--save-dir',"$review/saves",'--universe',"regional-v10-detail-$($view.seed)-$($view.mode)",'--catalogue',"$root/artifacts/galaxy/galaxy.bin",'--allow-test-identities') -RedirectStandardOutput "$out/server.log" -RedirectStandardError "$out/server-error.log";$owned+=$s
 $watch=[Diagnostics.Stopwatch]::StartNew()
 while(!((Get-Content "$out/server.log" -Raw -ErrorAction SilentlyContinue)-match 'Listening')){if($s.HasExited){throw "Server failed: $out"};if($watch.Elapsed.TotalSeconds -gt 90){throw 'Server startup timeout'};Start-Sleep -Milliseconds 200}
 $c=Start-Process dotnet -WindowStyle Hidden -PassThru -WorkingDirectory $root -ArgumentList @("$bin/StargateGame.Client/release/StargateGame.Client.dll",'--host','127.0.0.1','--port',"$port",'--name','RegionalReview','--identity-profile','RegionalReview','--dev-assets',"$root/artifacts/cooked/dev-override",'--graphics','performance','--vsync','off','--view',$view.view,'--weather','off','--time-of-day',"$Hour",'--pause-sky','--benchmark-warmup',"$Warmup",'--benchmark-seconds',"$Seconds",'--exact-resolution','--width','1920','--height','1080','--capture-dir',$out) -RedirectStandardOutput "$out/client.log" -RedirectStandardError "$out/client-error.log";$owned+=$c
 while(!$c.HasExited){if($watch.Elapsed.TotalSeconds -gt ($Warmup+$Seconds+150)){throw 'Client inspection timeout'};Start-Sleep -Milliseconds 250}
 $c.WaitForExit();if($c.ExitCode -ne 0){throw "Client failed: $out"}
 Write-Output "REGIONAL CAPTURE $out"
}finally{foreach($p in $owned){if(!$p.HasExited){Stop-Process -Id $p.Id}};$env:STARGATEGAME_TERRAIN_CACHE=$previousCache}
