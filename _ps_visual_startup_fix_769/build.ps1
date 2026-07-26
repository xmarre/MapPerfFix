$ErrorActionPreference = 'Stop'

$workspace = $env:GITHUB_WORKSPACE
$input = Join-Path $workspace '_ps_visual_startup_fix_769/input'
$expectedHashes = @{
    'source.patch' = 'ac71cb6b31ef2fae332949164b1ad2d286169637d8e3818cbe16974edf69fec5'
    'PlayerSettlementBehaviour.cs' = 'd61dfc9a8773808219a7c864cc0f235a2266e4f337cbffaff8232d8df56d6323'
    'MapScreenPatch.cs' = '3af8bffe857ffd64bc5ffc5611eabd1f3d2d22c596821665d1480a7877c12641'
    'SaveHandler.cs' = '126c75a557265c6619b7d7cb09e3d322316d3032a67f84359cd15153ac284b9d'
    'CultureVisualHelper.cs' = 'bba0336eab51535ecfd594098df09bf94827b9b7f95f29c5c54294a2440c0e62'
    'patch_visual_startup.py' = 'de1ec95b9e121ba72767c6b8d98e216afb77685e58a739a8dde65b45c1df1824'
}
foreach ($entry in $expectedHashes.GetEnumerator()) {
    $path = Join-Path $input $entry.Key
    if (-not (Test-Path $path)) { throw "SOURCE INPUT missing: $($entry.Key)" }
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Value) {
        throw "SOURCE INPUT SHA-256 mismatch for $($entry.Key). Expected=$($entry.Value) Actual=$actual"
    }
    Write-Host "SOURCE INPUT SHA-256 verified: $($entry.Key) $actual"
}
$actualInputHash = ($expectedHashes.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ';'

$root = Join-Path $workspace '_ps_visual_startup_fix_769_build'
$src = Join-Path $root 'source'
$out = Join-Path $root 'out'
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $root,$out | Out-Null

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
