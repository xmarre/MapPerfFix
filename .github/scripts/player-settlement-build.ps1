$ErrorActionPreference = 'Stop'
$payload = Join-Path $env:GITHUB_WORKSPACE '_player_settlement_build'
$src = Join-Path $payload 'source'
$out = Join-Path $payload 'out'
New-Item -ItemType Directory -Force -Path $payload,$out | Out-Null

try {
    git clone --filter=blob:none --no-checkout https://github.com/BOTLANNER/BannerlordPlayerSettlement.git $src 2>&1 | Out-Null
    git -C $src checkout 52d86c7480778afb83e476ac742895f73fbf6d7f -- . 2>&1 | Out-Null

    function Get-LatestPackageVersion([string]$id, [string]$prefix) {
        $index = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/index.json"
        $matches = @($index.versions | Where-Object { $_ -like "$prefix*" })
        if ($matches.Count -eq 0) { throw "No $id package version starts with $prefix" }
        return $matches[-1]
    }

    $coreVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.Core' '1.3.15.'
    $nativeVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.Native' '1.3.15.'
    $sandboxVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.SandBox' '1.3.15.'
    $storyVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.StoryMode' '1.3.15.'
    "Core=$coreVersion`nNative=$nativeVersion`nSandBox=$sandboxVersion`nStoryMode=$storyVersion" | Set-Content -Encoding UTF8 (Join-Path $out 'reference-versions.txt')
    Write-Host "References: Core=$coreVersion Native=$nativeVersion SandBox=$sandboxVersion StoryMode=$storyVersion"

    $project = Join-Path $src 'BannerlordPlayerSettlement\BannerlordPlayerSettlement.csproj'
    $xml = Get-Content -Raw $project
    $xml = $xml -replace '<Version>7\.5\.0</Version>', '<Version>7.5.1</Version>'
    $xml = $xml -replace '<GameVersion Condition="\$\(IsStable\) == ''true''">[^<]+</GameVersion>', '<GameVersion Condition="$(IsStable) == ''true''">1.3.15</GameVersion>'
    $xml = $xml -replace '<GameVersion Condition="\$\(IsBeta\) == ''true''">[^<]+</GameVersion>', '<GameVersion Condition="$(IsBeta) == ''true''">1.3.15</GameVersion>'
    $xml = $xml -replace '(?s)\s*<ItemGroup>\s*<!-- Bannerlord Native Assemblies -->.*?</ItemGroup>', ''
    $xml = $xml -replace '(?s)\s*<ItemGroup>\s*<Reference Update="\$\(GameFolder\).*?</ItemGroup>', ''
    $packageRefs = @"
    <PackageReference Include="System.Numerics.Vectors" Version="4.5.0" />
    <PackageReference Include="Bannerlord.UIExtenderEx" Version="2.13.2" IncludeAssets="compile" />
    <PackageReference Include="Bannerlord.ButterLib" Version="2.10.2" IncludeAssets="compile" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.Core" Version="$coreVersion" PrivateAssets="all" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.Native" Version="$nativeVersion" PrivateAssets="all" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.SandBox" Version="$sandboxVersion" PrivateAssets="all" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.StoryMode" Version="$storyVersion" PrivateAssets="all" />
"@
    $xml = $xml -replace '<PackageReference Include="System\.Numerics\.Vectors" Version="4\.5\.0" />', $packageRefs.TrimEnd()
    $xml = $xml -replace '(?s)\s*<Target Name="PostBuild".*?</Target>', ''
    Set-Content -Encoding UTF8 $project $xml

    git -C $src diff | Set-Content -Encoding UTF8 (Join-Path $out 'source.patch')
    Copy-Item $project (Join-Path $out 'BannerlordPlayerSettlement.csproj') -Force

    & dotnet build $project -c Beta_Release -p:Platform=x64 --nologo -v:minimal *> (Join-Path $out 'build.log')
    $buildExit = $LASTEXITCODE
    if ($buildExit -ne 0) {
        Write-Host '=== Focused build errors ==='
        Select-String -Path (Join-Path $out 'build.log') -Pattern 'error CS|error NU|error MSB|: error ' | Select-Object -Last 100 | ForEach-Object { Write-Host $_.Line }
        throw "dotnet build failed with exit code $buildExit"
    }

    $built = Get-ChildItem (Join-Path $src 'BannerlordPlayerSettlement\bin') -Recurse -Filter 'PlayerSettlement.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $built) { throw 'PlayerSettlement.dll was not produced' }
    Copy-Item $built.FullName (Join-Path $out 'PlayerSettlement.dll') -Force
    $pdb = [System.IO.Path]::ChangeExtension($built.FullName, '.pdb')
    if (Test-Path $pdb) { Copy-Item $pdb (Join-Path $out 'PlayerSettlement.pdb') -Force }
    Compress-Archive -Path (Join-Path $src 'PlayerSettlement_TOR'),(Join-Path $src 'BannerlordPlayerSettlement\_Module') -DestinationPath (Join-Path $out 'module-assets.zip') -Force
    Write-Host "Compiled: $($built.FullName)"
}
catch {
    Write-Host "FATAL: $($_.Exception.Message)"
    ($_ | Out-String) | Set-Content -Encoding UTF8 (Join-Path $out 'fatal.log')
    throw
}
