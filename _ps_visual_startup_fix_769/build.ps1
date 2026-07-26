$ErrorActionPreference = 'Stop'

$workspace = $env:GITHUB_WORKSPACE
$root = Join-Path $workspace '_ps_visual_startup_fix_769_build'
$src = Join-Path $root 'source'
$out = Join-Path $root 'out'
$downloadedNative = Join-Path $workspace '_downloaded_native'
$input = Join-Path $workspace '_ps_visual_startup_fix_769/input'
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $root,$out | Out-Null

# Enforce exact hashes before any source is applied.
$nativePatch = Get-ChildItem $downloadedNative -Recurse -Filter 'source.patch' | Select-Object -First 1
if (-not $nativePatch) { throw 'Validated 7.5.4 source.patch artifact not found' }
$expectedNativePatchHash = '0a29aad0def0e5a71f211ebeb0c7258abc9c80e4679286ba9356a171cd6234f1'
$actualNativePatchHash = (Get-FileHash $nativePatch.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualNativePatchHash -ne $expectedNativePatchHash) {
    throw "7.5.4 source.patch SHA-256 mismatch. Expected=$expectedNativePatchHash Actual=$actualNativePatchHash"
}
Write-Host "Verified 7.5.4 source.patch SHA-256: $actualNativePatchHash"

$verifiedInputs = @{
    'CultureVisualHelper.cs' = 'bba0336eab51535ecfd594098df09bf94827b9b7f95f29c5c54294a2440c0e62'
    'patch_visual_startup.py' = 'dc2c61dd09589f9fe8cb2135295a9422b75d5f9e3f001743661c01d8cb19812f'
    'patch_behavior_769.py' = 'd797b06f39b358e456ebd80f5ae43194824f792054bacd4846efa87194c449d5'
    'patch_map_screen_769.py' = '25da486a9dc0903845e3a1593a7a38986c9484d92ceee5983547b5ae6710a4dd'
}
foreach ($entry in $verifiedInputs.GetEnumerator()) {
    $path = Join-Path $input $entry.Key
    if (-not (Test-Path $path)) { throw "Required source input missing: $($entry.Key)" }
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Value) {
        throw "Source input SHA-256 mismatch for $($entry.Key). Expected=$($entry.Value) Actual=$actual"
    }
    Write-Host "Verified source input SHA-256: $($entry.Key) $actual"
}

$upstreamCommit = '52d86c7480778afb83e476ac742895f73fbf6d7f'
$reloadCommit = 'fce517b787dbb83edb2cf2c3224b12588b8064f5'
$v757Commit = 'e437560ef05bdd8294f794be104e7413f4d1898f'
$v759Commit = 'a4e28c27eee7a4074d5f21f894ef0f08d97142cc'
$v760Commit = '1aaab102123f7d48fae7ae002ae3b77550e033af'
$v761Commit = 'fdd406f94d9f885ed961abd7b068fd6f868ade96'
$v762Commit = '8a005b93c5a5c41f2a1445f587f0b9cfb3fd9cb3'

git clone https://github.com/BOTLANNER/BannerlordPlayerSettlement.git $src
if ($LASTEXITCODE -ne 0) { throw "git clone failed: $LASTEXITCODE" }
git -C $src checkout $upstreamCommit
if ($LASTEXITCODE -ne 0) { throw "git checkout failed: $LASTEXITCODE" }
if ((git -C $src rev-parse HEAD).Trim() -ne $upstreamCommit) { throw 'Unexpected upstream source commit' }

git -C $src apply $nativePatch.FullName
if ($LASTEXITCODE -ne 0) { throw "7.5.4 source patch failed: $LASTEXITCODE" }

$behaviourPath = Join-Path $src 'BannerlordPlayerSettlement/Behaviours/PlayerSettlementBehaviour.cs'
$mapPatchPath = Join-Path $src 'BannerlordPlayerSettlement/Patches/MapScreenPatch.cs'
$saveHandlerPath = Join-Path $src 'BannerlordPlayerSettlement/SaveHandler.cs'
$partyVisualPath = Join-Path $src 'BannerlordPlayerSettlement/Patches/PartyVisualPatch.cs'
$patchDir = Join-Path $src 'BannerlordPlayerSettlement/Patches'

$mainPatchScript = Join-Path $root 'patch_main_v756.py'
$v759GatePatchScript = Join-Path $root 'patch_gate_workflow_v759.py'
$v760GateRequiredScript = Join-Path $root 'patch_gate_required_v760.py'
$v760MapClickScript = Join-Path $root 'patch_map_click_v760.py'
$v761GateScript = Join-Path $root 'patch_gate_commit_v761.py'
$v761MapScript = Join-Path $root 'patch_map_click_v761.py'
$v762GateScript = Join-Path $root 'patch_gate_tick_v762.py'
$v762MapScript = Join-Path $root 'patch_map_click_v762.py'

Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$reloadCommit/_temp_player_settlement_v755/patch_main_v756.py" -OutFile $mainPatchScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v757Commit/_temp_player_settlement_v757/LifecycleSafetyPatches.cs" -OutFile (Join-Path $patchDir 'LifecycleSafetyPatches.cs')
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v759Commit/_temp_player_settlement_v759/patch_gate_workflow_v759.py" -OutFile $v759GatePatchScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v760Commit/_temp_player_settlement_v760/patch_gate_required_v760.py" -OutFile $v760GateRequiredScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v760Commit/_temp_player_settlement_v760/patch_map_click_v760.py" -OutFile $v760MapClickScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v761Commit/_temp_player_settlement_v761/patch_gate_commit_v761.py" -OutFile $v761GateScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v761Commit/_temp_player_settlement_v761/patch_map_click_v761.py" -OutFile $v761MapScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v762Commit/_temp_player_settlement_v762/patch_gate_tick_v762.py" -OutFile $v762GateScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v762Commit/_temp_player_settlement_v762/patch_map_click_v762.py" -OutFile $v762MapScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v760Commit/_temp_player_settlement_v760/SaveHandler.cs" -OutFile $saveHandlerPath

python $mainPatchScript (Join-Path $src 'BannerlordPlayerSettlement/Main.cs')
if ($LASTEXITCODE -ne 0) { throw "Main lifecycle patch failed: $LASTEXITCODE" }
python $v759GatePatchScript $behaviourPath
if ($LASTEXITCODE -ne 0) { throw "7.5.9 gate patch failed: $LASTEXITCODE" }
python $v760GateRequiredScript $behaviourPath
if ($LASTEXITCODE -ne 0) { throw "7.6.0 mandatory gate patch failed: $LASTEXITCODE" }
python $v760MapClickScript $mapPatchPath
if ($LASTEXITCODE -ne 0) { throw "7.6.0 map click patch failed: $LASTEXITCODE" }
python $v761GateScript $behaviourPath
if ($LASTEXITCODE -ne 0) { throw "7.6.1 gate patch failed: $LASTEXITCODE" }
python $v761MapScript $mapPatchPath
if ($LASTEXITCODE -ne 0) { throw "7.6.1 map patch failed: $LASTEXITCODE" }
python $v762GateScript $behaviourPath
if ($LASTEXITCODE -ne 0) { throw "7.6.2 gate tick patch failed: $LASTEXITCODE" }
python $v762MapScript $mapPatchPath
if ($LASTEXITCODE -ne 0) { throw "7.6.2 map click suppression patch failed: $LASTEXITCODE" }

Copy-Item (Join-Path $input 'CultureVisualHelper.cs') (Join-Path $patchDir 'CultureVisualHelper.cs') -Force
python (Join-Path $input 'patch_behavior_769.py') $behaviourPath
if ($LASTEXITCODE -ne 0) { throw "7.6.9 behavior patch failed: $LASTEXITCODE" }
python (Join-Path $input 'patch_visual_startup.py') $partyVisualPath
if ($LASTEXITCODE -ne 0) { throw "7.6.9 visual startup patch failed: $LASTEXITCODE" }
python (Join-Path $input 'patch_map_screen_769.py') $mapPatchPath
if ($LASTEXITCODE -ne 0) { throw "7.6.9 map screen safety patch failed: $LASTEXITCODE" }

$project = Join-Path $src 'BannerlordPlayerSettlement/BannerlordPlayerSettlement.csproj'
$projectText = Get-Content -Raw $project
$projectText = [regex]::Replace($projectText, '<Version>[^<]+</Version>', '<Version>7.6.9</Version>', 1)
Set-Content -Encoding UTF8 $project $projectText

# Exact regression gates for the diagnosed visual startup failure and retained release behavior.
$partyText = Get-Content -Raw $partyVisualPath
$behaviourText = Get-Content -Raw $behaviourPath
$mapText = Get-Content -Raw $mapPatchPath
if ($partyText.Contains('gameEntity.Parent.GetUpgradeLevelOfEntity()')) { throw 'Unsafe map-banner parent dereference remains' }
if ($partyText.Contains('SetStrategicEntity.Invoke(__instance')) { throw 'Unsafe strategic entity setter invocation remains' }
if ($partyText.Contains('matricesFrame.ToArray()')) { throw 'Unsafe null siege-frame conversion remains' }
if (-not $partyText.Contains('handlingCustomVisual')) { throw 'Custom visual failure boundary missing' }
if (-not $partyText.Contains('CultureVisualHelper.TryCopyCreatedSettlementVisual')) { throw 'Created ToR settlement visual fallback missing' }
if (-not $partyText.Contains('LogManager.Log.SilentException(e);')) { throw 'Non-spamming custom visual diagnostic missing' }
if (-not $behaviourText.Contains('CultureVisualHelper.TryCopyExistingSettlementVisual')) { throw 'Exact-culture preview visual path missing' }
if (-not $behaviourText.Contains('castleSettlement.Town.OwnerClan = null;')) { throw 'Castle ownership lifecycle repair missing' }
if (-not $behaviourText.Contains('townSettlement.Town.OwnerClan = null;')) { throw 'Town ownership lifecycle repair missing' }
if ($behaviourText.Contains('UpdateSettlementVisualEntity(forward, retry: false);')) { throw 'Cross-culture retry remains' }
if (-not $mapText.Contains('GetFrameAndVisualOfEngines?.Invoke')) { throw 'Map visual dictionary accessor guard missing' }
if (Select-String -Path $saveHandlerPath -SimpleMatch 'TryLoadSave') { throw 'Unsafe in-process load remains' }
if (Select-String -Path $saveHandlerPath -SimpleMatch 'StartNewGame') { throw 'Unsafe campaign restart remains' }

& dotnet build $project -c Beta_Release -p:Platform=x64 --nologo -v:minimal *> (Join-Path $out 'build.log')
if ($LASTEXITCODE -ne 0) {
    Get-Content (Join-Path $out 'build.log') | Select-Object -Last 450 | ForEach-Object { Write-Host $_ }
    throw "dotnet build failed: $LASTEXITCODE"
}

$built = Get-ChildItem (Join-Path $src 'BannerlordPlayerSettlement/bin') -Recurse -Filter 'PlayerSettlement.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw 'PlayerSettlement.dll was not produced' }
$builtPdb = [IO.Path]::ChangeExtension($built.FullName, '.pdb')
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($built.FullName).Version.ToString()
if ($assemblyVersion -ne '7.6.9.0') { throw "Unexpected assembly version: $assemblyVersion" }
$references = [Reflection.AssemblyName]::GetAssemblyName($built.FullName) | Out-Null
$loadedAssembly = [Reflection.Assembly]::ReflectionOnlyLoadFrom($built.FullName)
if ($loadedAssembly.GetReferencedAssemblies().Name -contains 'PlayerSettlementCultureVisuals') {
    throw 'External PlayerSettlementCultureVisuals assembly reference remains'
}

Copy-Item $built.FullName (Join-Path $out 'PlayerSettlement.dll') -Force
if (Test-Path $builtPdb) { Copy-Item $builtPdb (Join-Path $out 'PlayerSettlement.pdb') -Force }
Copy-Item $partyVisualPath (Join-Path $out 'PartyVisualPatch.cs') -Force
Copy-Item $behaviourPath (Join-Path $out 'PlayerSettlementBehaviour.cs') -Force
Copy-Item $mapPatchPath (Join-Path $out 'MapScreenPatch.cs') -Force
Copy-Item $saveHandlerPath (Join-Path $out 'SaveHandler.cs') -Force
Copy-Item (Join-Path $patchDir 'CultureVisualHelper.cs') (Join-Path $out 'CultureVisualHelper.cs') -Force
Copy-Item $project (Join-Path $out 'BannerlordPlayerSettlement.csproj') -Force
git -C $src diff | Set-Content -Encoding UTF8 (Join-Path $out 'source_7.6.9.patch')

$dllHash = (Get-FileHash $built.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$pdbHash = if (Test-Path $builtPdb) { (Get-FileHash $builtPdb -Algorithm SHA256).Hash.ToLowerInvariant() } else { '<not produced>' }
@"
Version=$assemblyVersion
UpstreamCommit=$upstreamCommit
ValidatedNativePatchSHA256=$actualNativePatchHash
PlayerSettlementDLL_SHA256=$dllHash
PlayerSettlementPDB_SHA256=$pdbHash
Fix=SettlementVisual.OnStartup null-safety; created ToR visual fallback; exact-culture preview; ownership lifecycle; integrated culture helper
"@ | Set-Content -Encoding UTF8 (Join-Path $out 'BUILD_INFO_7.6.9.txt')

Write-Host "PlayerSettlement.dll version: $assemblyVersion"
Write-Host "PlayerSettlement.dll SHA-256: $dllHash"
