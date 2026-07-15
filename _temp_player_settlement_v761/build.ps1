$ErrorActionPreference = 'Stop'

$root = Join-Path $env:GITHUB_WORKSPACE '_player_settlement_v761_build'
$src = Join-Path $root 'source'
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force -Path $root,$out | Out-Null

$expectedCommit = '52d86c7480778afb83e476ac742895f73fbf6d7f'
$reloadCommit = 'fce517b787dbb83edb2cf2c3224b12588b8064f5'
$v757Commit = 'e437560ef05bdd8294f794be104e7413f4d1898f'
$v759Commit = 'a4e28c27eee7a4074d5f21f894ef0f08d97142cc'
$v760Commit = '1aaab102123f7d48fae7ae002ae3b77550e033af'

git clone https://github.com/BOTLANNER/BannerlordPlayerSettlement.git $src
if ($LASTEXITCODE -ne 0) { throw "git clone failed: $LASTEXITCODE" }
git -C $src checkout $expectedCommit
if ($LASTEXITCODE -ne 0) { throw "git checkout failed: $LASTEXITCODE" }

$nativeArtifact = Join-Path $env:GITHUB_WORKSPACE '_downloaded_native'
$nativePatch = Get-ChildItem $nativeArtifact -Recurse -Filter 'source.patch' | Select-Object -First 1
if (-not $nativePatch) { throw '7.5.4 native-visual source.patch not found' }

git -C $src apply $nativePatch.FullName
if ($LASTEXITCODE -ne 0) { throw "native-visual patch failed: $LASTEXITCODE" }

$behaviourPath = Join-Path $src 'BannerlordPlayerSettlement\Behaviours\PlayerSettlementBehaviour.cs'
$mapPatchPath = Join-Path $src 'BannerlordPlayerSettlement\Patches\MapScreenPatch.cs'
$saveHandlerPath = Join-Path $src 'BannerlordPlayerSettlement\SaveHandler.cs'
$mainPath = Join-Path $src 'BannerlordPlayerSettlement\Main.cs'
$patchDir = Join-Path $src 'BannerlordPlayerSettlement\Patches'
$mainPatchScript = Join-Path $root 'patch_main_v756.py'
$v759GatePatchScript = Join-Path $root 'patch_gate_workflow_v759.py'
$v760GateRequiredScript = Join-Path $root 'patch_gate_required_v760.py'
$v760MapClickScript = Join-Path $root 'patch_map_click_v760.py'

Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$reloadCommit/_temp_player_settlement_v755/patch_main_v756.py" -OutFile $mainPatchScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v757Commit/_temp_player_settlement_v757/LifecycleSafetyPatches.cs" -OutFile (Join-Path $patchDir 'LifecycleSafetyPatches.cs')
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v759Commit/_temp_player_settlement_v759/patch_gate_workflow_v759.py" -OutFile $v759GatePatchScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v760Commit/_temp_player_settlement_v760/patch_gate_required_v760.py" -OutFile $v760GateRequiredScript
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v760Commit/_temp_player_settlement_v760/patch_map_click_v760.py" -OutFile $v760MapClickScript

python $mainPatchScript $mainPath
if ($LASTEXITCODE -ne 0) { throw "Main.cs lifecycle patch failed: $LASTEXITCODE" }

Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$v760Commit/_temp_player_settlement_v760/SaveHandler.cs" -OutFile $saveHandlerPath

python $v759GatePatchScript $behaviourPath
if ($LASTEXITCODE -ne 0) { throw "7.5.9 gate workflow base patch failed: $LASTEXITCODE" }
python $v760GateRequiredScript $behaviourPath
if ($LASTEXITCODE -ne 0) { throw "7.6.0 mandatory gate patch failed: $LASTEXITCODE" }
python $v760MapClickScript $mapPatchPath
if ($LASTEXITCODE -ne 0) { throw "7.6.0 map click base patch failed: $LASTEXITCODE" }
python (Join-Path $env:GITHUB_WORKSPACE '_temp_player_settlement_v761\patch_gate_commit_v761.py') $behaviourPath
if ($LASTEXITCODE -ne 0) { throw "7.6.1 direct gate commit patch failed: $LASTEXITCODE" }
python (Join-Path $env:GITHUB_WORKSPACE '_temp_player_settlement_v761\patch_map_click_v761.py') $mapPatchPath
if ($LASTEXITCODE -ne 0) { throw "7.6.1 direct map click patch failed: $LASTEXITCODE" }

$project = Join-Path $src 'BannerlordPlayerSettlement\BannerlordPlayerSettlement.csproj'
$projectText = Get-Content -Raw $project
$projectText = $projectText.Replace('<Version>7.5.4</Version>', '<Version>7.6.1</Version>')
Set-Content -Encoding UTF8 $project $projectText

if (Select-String -Path $saveHandlerPath -SimpleMatch 'TryLoadSave') { throw 'Unsafe TryLoadSave call remains' }
if (Select-String -Path $saveHandlerPath -SimpleMatch 'StartNewGame') { throw 'Unsafe StartNewGame call remains' }
if (Select-String -Path $saveHandlerPath -SimpleMatch 'EndGame') { throw 'Unsafe EndGame call remains' }
if (-not (Select-String -Path $saveHandlerPath -SimpleMatch 'Saving placement without in-process reload')) { throw 'Save-and-continue handler missing' }
if (Select-String -Path $behaviourPath -SimpleMatch 'AllowGatePosition && gatePlacementFrame') { throw 'Persisted gate setting still discards committed frame' }
if (Select-String -Path $behaviourPath -SimpleMatch 'FaceIslandIndex == MobileParty.MainParty.CurrentNavigationFace.FaceIslandIndex') { throw 'Brittle gate island restriction remains' }
if (-not (Select-String -Path $behaviourPath -SimpleMatch 'CommitGatePlacement(CampaignVec2 clickPosition)')) { throw 'Direct gate commit method missing' }
if (-not (Select-String -Path $behaviourPath -SimpleMatch 'Committed gate position directly')) { throw 'Gate commit diagnostic missing' }
if (-not (Select-String -Path $mapPatchPath -SimpleMatch 'CommitGatePlacement(intersectionPoint)')) { throw 'Map click does not commit gate coordinates directly' }
if (-not (Select-String -Path $mapPatchPath -SimpleMatch 'CommitSettlementPlacement(intersectionPoint)')) { throw 'Map click does not commit settlement coordinates directly' }
if (Test-Path (Join-Path $patchDir 'GlobalCampaignTickSafetyPatch.cs')) { throw 'Obsolete global tick patch must not be present' }
if (Test-Path (Join-Path $patchDir 'PlayerArmyWaitSafetyPatch.cs')) { throw 'Obsolete army wait patch must not be present' }

git -C $src diff | Set-Content -Encoding UTF8 (Join-Path $out 'source.patch')
Copy-Item $saveHandlerPath (Join-Path $out 'SaveHandler.cs') -Force
Copy-Item $behaviourPath (Join-Path $out 'PlayerSettlementBehaviour.cs') -Force
Copy-Item $mapPatchPath (Join-Path $out 'MapScreenPatch.cs') -Force

& dotnet build $project -c Beta_Release -p:Platform=x64 --nologo -v:minimal *> (Join-Path $out 'build.log')
if ($LASTEXITCODE -ne 0) {
    Get-Content (Join-Path $out 'build.log') | Select-Object -Last 360 | ForEach-Object { Write-Host $_ }
    throw "dotnet build failed: $LASTEXITCODE"
}

$built = Get-ChildItem (Join-Path $src 'BannerlordPlayerSettlement\bin') -Recurse -Filter 'PlayerSettlement.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw 'PlayerSettlement.dll was not produced' }
$builtPdb = [IO.Path]::ChangeExtension($built.FullName, '.pdb')
Copy-Item $built.FullName (Join-Path $out 'PlayerSettlement.dll') -Force
if (Test-Path $builtPdb) { Copy-Item $builtPdb (Join-Path $out 'PlayerSettlement.pdb') -Force }

$version = [Reflection.AssemblyName]::GetAssemblyName($built.FullName).Version.ToString()
if ($version -ne '7.6.1.0') { throw "Unexpected assembly version: $version" }

$dllHash = (Get-FileHash $built.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
@"
AssemblyVersion=$version
DLL_SHA256=$dllHash
UpstreamCommit=$expectedCommit
Fixes=direct click-coordinate commits for settlement/gate/port; remove ToR-brittle FaceIslandIndex validation; always serialize mandatory gate frame; gate marker starts at settlement
"@ | Set-Content -Encoding UTF8 (Join-Path $out 'BUILD_INFO.txt')

Write-Host "PlayerSettlement.dll version: $version"
Write-Host "SHA256: $dllHash"
