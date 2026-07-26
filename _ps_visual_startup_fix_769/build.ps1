$ErrorActionPreference = 'Stop'

$workspace = $env:GITHUB_WORKSPACE
$inputZip = Join-Path $workspace '_ps_visual_startup_fix_769/PlayerSettlement_7.6.9_SOURCE_INPUT.zip'
$expectedInputHash = 'f298b5c9b8ea961845cf15d45b5a2985f2e0b5cfcec7c497c47be6f880f87785'
$actualInputHash = (Get-FileHash $inputZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualInputHash -ne $expectedInputHash) {
    throw "SOURCE INPUT SHA-256 mismatch. Expected=$expectedInputHash Actual=$actualInputHash"
}
Write-Host "SOURCE INPUT SHA-256 verified: $actualInputHash"

$root = Join-Path $workspace '_ps_visual_startup_fix_769_build'
$input = Join-Path $root 'input'
$src = Join-Path $root 'source'
$out = Join-Path $root 'out'
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $root,$input,$out | Out-Null
Expand-Archive -Path $inputZip -DestinationPath $input -Force

$upstreamCommit = '52d86c7480778afb83e476ac742895f73fbf6d7f'
git clone https://github.com/BOTLANNER/BannerlordPlayerSettlement.git $src
if ($LASTEXITCODE -ne 0) { throw "git clone failed: $LASTEXITCODE" }
git -C $src checkout $upstreamCommit
if ($LASTEXITCODE -ne 0) { throw "git checkout failed: $LASTEXITCODE" }
$resolvedCommit = (git -C $src rev-parse HEAD).Trim()
if ($resolvedCommit -ne $upstreamCommit) { throw "Upstream commit mismatch: $resolvedCommit" }

git -C $src apply (Join-Path $input 'source.patch')
if ($LASTEXITCODE -ne 0) { throw "7.6.2 source patch failed: $LASTEXITCODE" }

Copy-Item (Join-Path $input 'PlayerSettlementBehaviour.cs') (Join-Path $src 'BannerlordPlayerSettlement/Behaviours/PlayerSettlementBehaviour.cs') -Force
Copy-Item (Join-Path $input 'MapScreenPatch.cs') (Join-Path $src 'BannerlordPlayerSettlement/Patches/MapScreenPatch.cs') -Force
Copy-Item (Join-Path $input 'SaveHandler.cs') (Join-Path $src 'BannerlordPlayerSettlement/SaveHandler.cs') -Force
Copy-Item (Join-Path $input 'CultureVisualHelper.cs') (Join-Path $src 'BannerlordPlayerSettlement/Patches/CultureVisualHelper.cs') -Force

$partyVisualPath = Join-Path $src 'BannerlordPlayerSettlement/Patches/PartyVisualPatch.cs'
python (Join-Path $input 'patch_visual_startup.py') $partyVisualPath
if ($LASTEXITCODE -ne 0) { throw "PartyVisualPatch startup repair failed: $LASTEXITCODE" }

$project = Join-Path $src 'BannerlordPlayerSettlement/BannerlordPlayerSettlement.csproj'
$projectText = Get-Content -Raw $project
$projectText = $projectText.Replace('<Version>7.6.2</Version>', '<Version>7.6.9</Version>')
Set-Content -Encoding UTF8 $project $projectText

# Regression gates for the exact failure captured by 7.6.8.2 diagnostics.
$partyText = Get-Content -Raw $partyVisualPath
if ($partyText.Contains('gameEntity.Parent.GetUpgradeLevelOfEntity()')) { throw 'Unsafe map-banner parent dereference remains' }
if ($partyText.Contains('SetStrategicEntity.Invoke(__instance')) { throw 'Unsafe strategic entity setter invocation remains' }
if ($partyText.Contains('matricesFrame.ToArray()')) { throw 'Unsafe null siege-frame conversion remains' }
if (-not $partyText.Contains('return false;') -or -not $partyText.Contains('handlingCustomVisual')) { throw 'Custom visual fail-closed path missing' }
if (-not $partyText.Contains('SilentException')) { throw 'Silent diagnostic path missing' }
if (-not (Test-Path (Join-Path $src 'BannerlordPlayerSettlement/Patches/CultureVisualHelper.cs'))) { throw 'Integrated culture visual helper missing' }

& dotnet build $project -c Beta_Release -p:Platform=x64 --nologo -v:minimal *> (Join-Path $out 'build.log')
if ($LASTEXITCODE -ne 0) {
    Get-Content (Join-Path $out 'build.log') | Select-Object -Last 400 | ForEach-Object { Write-Host $_ }
    throw "dotnet build failed: $LASTEXITCODE"
}

$built = Get-ChildItem (Join-Path $src 'BannerlordPlayerSettlement/bin') -Recurse -Filter 'PlayerSettlement.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw 'PlayerSettlement.dll was not produced' }
$builtPdb = [IO.Path]::ChangeExtension($built.FullName, '.pdb')
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($built.FullName).Version.ToString()
if ($assemblyVersion -ne '7.6.9.0') { throw "Unexpected assembly version: $assemblyVersion" }

Copy-Item $built.FullName (Join-Path $out 'PlayerSettlement.dll') -Force
if (Test-Path $builtPdb) { Copy-Item $builtPdb (Join-Path $out 'PlayerSettlement.pdb') -Force }
Copy-Item $partyVisualPath (Join-Path $out 'PartyVisualPatch.cs') -Force
Copy-Item (Join-Path $src 'BannerlordPlayerSettlement/Patches/CultureVisualHelper.cs') (Join-Path $out 'CultureVisualHelper.cs') -Force
Copy-Item (Join-Path $src 'BannerlordPlayerSettlement/Behaviours/PlayerSettlementBehaviour.cs') (Join-Path $out 'PlayerSettlementBehaviour.cs') -Force
Copy-Item $project (Join-Path $out 'BannerlordPlayerSettlement.csproj') -Force
git -C $src diff | Set-Content -Encoding UTF8 (Join-Path $out 'source_7.6.9.patch')

$dllHash = (Get-FileHash $built.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$pdbHash = if (Test-Path $builtPdb) { (Get-FileHash $builtPdb -Algorithm SHA256).Hash.ToLowerInvariant() } else { '<not produced>' }
@"
Version=7.6.9.0
UpstreamCommit=$upstreamCommit
SourceInputSHA256=$actualInputHash
PlayerSettlementDLL_SHA256=$dllHash
PlayerSettlementPDB_SHA256=$pdbHash
Fix=SettlementVisual.OnStartup null-safety; integrated culture visual helper; no external helper assembly dependency
"@ | Set-Content -Encoding UTF8 (Join-Path $out 'BUILD_INFO_7.6.9.txt')

Write-Host "PlayerSettlement.dll version: $assemblyVersion"
Write-Host "PlayerSettlement.dll SHA-256: $dllHash"
