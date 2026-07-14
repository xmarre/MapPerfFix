$ErrorActionPreference = 'Stop'

$root = Join-Path $env:GITHUB_WORKSPACE '_player_settlement_v755_build'
$src = Join-Path $root 'source'
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force -Path $root,$out | Out-Null

$expectedCommit = '52d86c7480778afb83e476ac742895f73fbf6d7f'
git clone https://github.com/BOTLANNER/BannerlordPlayerSettlement.git $src
if ($LASTEXITCODE -ne 0) { throw "git clone failed: $LASTEXITCODE" }
git -C $src checkout $expectedCommit
if ($LASTEXITCODE -ne 0) { throw "git checkout failed: $LASTEXITCODE" }

$nativeArtifact = Join-Path $env:GITHUB_WORKSPACE '_downloaded_native'
$nativePatch = Get-ChildItem $nativeArtifact -Recurse -Filter 'source.patch' | Select-Object -First 1
if (-not $nativePatch) { throw '7.5.4 native-visual source.patch not found' }

git -C $src apply $nativePatch.FullName
if ($LASTEXITCODE -ne 0) { throw "native-visual patch failed: $LASTEXITCODE" }

Copy-Item (Join-Path $env:GITHUB_WORKSPACE '_temp_player_settlement_v755\SaveHandler.cs') (Join-Path $src 'BannerlordPlayerSettlement\SaveHandler.cs') -Force

$project = Join-Path $src 'BannerlordPlayerSettlement\BannerlordPlayerSettlement.csproj'
$projectText = Get-Content -Raw $project
$projectText = $projectText.Replace('<Version>7.5.4</Version>', '<Version>7.5.5</Version>')
Set-Content -Encoding UTF8 $project $projectText

git -C $src diff | Set-Content -Encoding UTF8 (Join-Path $out 'source.patch')

& dotnet build $project -c Beta_Release -p:Platform=x64 --nologo -v:minimal *> (Join-Path $out 'build.log')
if ($LASTEXITCODE -ne 0) {
    Get-Content (Join-Path $out 'build.log') | Select-Object -Last 200 | ForEach-Object { Write-Host $_ }
    throw "dotnet build failed: $LASTEXITCODE"
}

$built = Get-ChildItem (Join-Path $src 'BannerlordPlayerSettlement\bin') -Recurse -Filter 'PlayerSettlement.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $built) { throw 'PlayerSettlement.dll was not produced' }
$builtPdb = [IO.Path]::ChangeExtension($built.FullName, '.pdb')
Copy-Item $built.FullName (Join-Path $out 'PlayerSettlement.dll') -Force
if (Test-Path $builtPdb) { Copy-Item $builtPdb (Join-Path $out 'PlayerSettlement.pdb') -Force }

$version = [Reflection.AssemblyName]::GetAssemblyName($built.FullName).Version.ToString()
if ($version -ne '7.5.5.0') { throw "Unexpected assembly version: $version" }

$dllHash = (Get-FileHash $built.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
@"
AssemblyVersion=$version
DLL_SHA256=$dllHash
UpstreamCommit=$expectedCommit
Fixes=deferred OnSaveOver reload; no live MapScreen PopScreen
"@ | Set-Content -Encoding UTF8 (Join-Path $out 'BUILD_INFO.txt')

Write-Host "PlayerSettlement.dll version: $version"
Write-Host "SHA256: $dllHash"
