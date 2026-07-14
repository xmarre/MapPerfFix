$ErrorActionPreference = 'Stop'
$root = Join-Path $env:GITHUB_WORKSPACE '_player_settlement_native_build'
$src = Join-Path $root 'source'
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force -Path $root,$out | Out-Null

try {
    $expectedCommit = '52d86c7480778afb83e476ac742895f73fbf6d7f'
    git clone https://github.com/BOTLANNER/BannerlordPlayerSettlement.git $src 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git clone failed: $LASTEXITCODE" }
    git -C $src checkout $expectedCommit 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git checkout failed: $LASTEXITCODE" }
    if ((git -C $src rev-parse HEAD).Trim() -ne $expectedCommit) { throw 'Unexpected upstream commit' }

    python (Join-Path $env:GITHUB_WORKSPACE '_temp_player_settlement_native\patch_all.py') $src
    if ($LASTEXITCODE -ne 0) { throw "patcher failed: $LASTEXITCODE" }

    $project = Join-Path $src 'BannerlordPlayerSettlement\BannerlordPlayerSettlement.csproj'
    $xml = Get-Content -Raw $project
    $xml = $xml -replace '<Version>7\.5\.0</Version>', '<Version>7.5.4</Version>'
    $xml = $xml -replace '<GameVersion Condition="\$\(IsStable\) == ''true''">[^<]+</GameVersion>', '<GameVersion Condition="$(IsStable) == ''true''">1.3.15</GameVersion>'
    $xml = $xml -replace '<GameVersion Condition="\$\(IsBeta\) == ''true''">[^<]+</GameVersion>', '<GameVersion Condition="$(IsBeta) == ''true''">1.3.15</GameVersion>'
    $xml = $xml -replace '(?s)\s*<ItemGroup>\s*<!-- Bannerlord Native Assemblies -->.*?</ItemGroup>', ''
    $xml = $xml -replace '(?s)\s*<ItemGroup>\s*<Reference Update="\$\(GameFolder\).*?</ItemGroup>', ''
    $refs = @'
    <PackageReference Include="System.Numerics.Vectors" Version="4.5.0" />
    <PackageReference Include="Bannerlord.UIExtenderEx" Version="2.13.2" IncludeAssets="compile" />
    <PackageReference Include="Bannerlord.ButterLib" Version="2.10.2" IncludeAssets="compile" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.Core" Version="1.3.15.110062" PrivateAssets="all" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.Native" Version="1.3.15.110062" PrivateAssets="all" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.SandBox" Version="1.3.15.110062" PrivateAssets="all" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.StoryMode" Version="1.3.15.110062" PrivateAssets="all" />
'@
    $xml = $xml -replace '<PackageReference Include="System\.Numerics\.Vectors" Version="4\.5\.0" />', $refs.TrimEnd()
    $xml = $xml -replace '(?s)\s*<Target Name="PostBuild".*?</Target>', ''
    Set-Content -Encoding UTF8 $project $xml

    git -C $src diff | Set-Content -Encoding UTF8 (Join-Path $out 'source.patch')
    & dotnet build $project -c Beta_Release -p:Platform=x64 --nologo -v:minimal *> (Join-Path $out 'build.log')
    if ($LASTEXITCODE -ne 0) {
        Get-Content (Join-Path $out 'build.log') | Select-Object -Last 200 | ForEach-Object { Write-Host $_ }
        throw "dotnet build failed: $LASTEXITCODE"
    }

    $dll = Get-ChildItem (Join-Path $src 'BannerlordPlayerSettlement\bin') -Recurse -Filter PlayerSettlement.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $dll) { throw 'PlayerSettlement.dll not produced' }
    Copy-Item $dll.FullName (Join-Path $out 'PlayerSettlement.dll') -Force
    $pdb = [IO.Path]::ChangeExtension($dll.FullName, '.pdb')
    if (Test-Path $pdb) { Copy-Item $pdb (Join-Path $out 'PlayerSettlement.pdb') -Force }
    Get-FileHash (Join-Path $out 'PlayerSettlement.dll') -Algorithm SHA256 | Format-List | Out-String | Set-Content -Encoding UTF8 (Join-Path $out 'SHA256.txt')
    Write-Host "Built $($dll.FullName)"
}
catch {
    $_ | Out-String | Set-Content -Encoding UTF8 (Join-Path $out 'fatal.log')
    throw
}