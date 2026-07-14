$ErrorActionPreference = 'Stop'

$root = Join-Path $env:GITHUB_WORKSPACE '_player_settlement_v757_build'
$src = Join-Path $root 'source'
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force -Path $root,$out | Out-Null

$expectedCommit = '52d86c7480778afb83e476ac742895f73fbf6d7f'
$reloadCommit = 'fce517b787dbb83edb2cf2c3224b12588b8064f5'
git clone https://github.com/BOTLANNER/BannerlordPlayerSettlement.git $src
if ($LASTEXITCODE -ne 0) { throw "git clone failed: $LASTEXITCODE" }
git -C $src checkout $expectedCommit
if ($LASTEXITCODE -ne 0) { throw "git checkout failed: $LASTEXITCODE" }

$nativeArtifact = Join-Path $env:GITHUB_WORKSPACE '_downloaded_native'
$nativePatch = Get-ChildItem $nativeArtifact -Recurse -Filter 'source.patch' | Select-Object -First 1
if (-not $nativePatch) { throw '7.5.4 native-visual source.patch not found' }

git -C $src apply $nativePatch.FullName
if ($LASTEXITCODE -ne 0) { throw "native-visual patch failed: $LASTEXITCODE" }

$saveHandlerPath = Join-Path $src 'BannerlordPlayerSettlement\SaveHandler.cs'
$mainPath = Join-Path $src 'BannerlordPlayerSettlement\Main.cs'
$mainPatchScript = Join-Path $root 'patch_main_v756.py'

Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$reloadCommit/_temp_player_settlement_v755/SaveHandler.cs" -OutFile $saveHandlerPath
Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/xmarre/MapPerfFix/$reloadCommit/_temp_player_settlement_v755/patch_main_v756.py" -OutFile $mainPatchScript

python $mainPatchScript $mainPath
if ($LASTEXITCODE -ne 0) { throw "Main.cs lifecycle patch failed: $LASTEXITCODE" }

python (Join-Path $env:GITHUB_WORKSPACE '_temp_player_settlement_v757\patch_save_handler_v757.py') $saveHandlerPath
if ($LASTEXITCODE -ne 0) { throw "SaveHandler.cs placement reset patch failed: $LASTEXITCODE" }

Copy-Item (Join-Path $env:GITHUB_WORKSPACE '_temp_player_settlement_v757\LifecycleSafetyPatches.cs') (Join-Path $src 'BannerlordPlayerSettlement\Patches\LifecycleSafetyPatches.cs') -Force

$project = Join-Path $src 'BannerlordPlayerSettlement\BannerlordPlayerSettlement.csproj'
$projectText = Get-Content -Raw $project
$projectText = $projectText.Replace('<Version>7.5.4</Version>', '<Version>7.5.7</Version>')
Set-Content -Encoding UTF8 $project $projectText

if (-not (Select-String -Path $saveHandlerPath -SimpleMatch 'Clearing completed placement state before save')) { throw 'Placement reset was not inserted' }
if (-not (Select-String -Path (Join-Path $src 'BannerlordPlayerSettlement\Patches\LifecycleSafetyPatches.cs') -SimpleMatch 'Settlement.CurrentSettlement')) { throw 'Post-load Tick guard missing' }
if (-not (Select-String -Path (Join-Path $src 'BannerlordPlayerSettlement\Patches\LifecycleSafetyPatches.cs') -SimpleMatch 'IsAnyInquiryActive')) { throw 'Inquiry guard missing' }

git -C $src diff | Set-Content -Encoding UTF8 (Join-Path $out 'source.patch')

& dotnet build $project -c Beta_Release -p:Platform=x64 --nologo -v:minimal *> (Join-Path $out 'build.log')
if ($LASTEXITCODE -ne 0) {
    Get-Content (Join-Path $out 'build.log') | Select-Object -Last 260 | ForEach-Object { Write-Host $_ }
    throw "dotnet build failed: $LASTEXITCODE"
}

$built = Get-ChildItem (Join-Path $src 'BannerlordPlayerSettlement\bin') -Recurse -Filter 'PlayerSettlement.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw 'PlayerSettlement.dll was not produced' }
$builtPdb = [IO.Path]::ChangeExtension($built.FullName, '.pdb')
Copy-Item $built.FullName (Join-Path $out 'PlayerSettlement.dll') -Force
if (Test-Path $builtPdb) { Copy-Item $builtPdb (Join-Path $out 'PlayerSettlement.pdb') -Force }

$version = [Reflection.AssemblyName]::GetAssemblyName($built.FullName).Version.ToString()
if ($version -ne '7.5.7.0') { throw "Unexpected assembly version: $version" }

$dllHash = (Get-FileHash $built.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
@"
AssemblyVersion=$version
DLL_SHA256=$dllHash
UpstreamCommit=$expectedCommit
ReloadLifecycleBase=$reloadCommit
Fixes=guard post-load Tick; block duplicate inquiry placement; clear completed placement state before save
"@ | Set-Content -Encoding UTF8 (Join-Path $out 'BUILD_INFO.txt')

Write-Host "PlayerSettlement.dll version: $version"
Write-Host "SHA256: $dllHash"
