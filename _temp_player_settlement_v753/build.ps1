$ErrorActionPreference = 'Stop'

$root = Join-Path $env:GITHUB_WORKSPACE '_player_settlement_v753_build'
$src = Join-Path $root 'source'
$out = Join-Path $root 'out'
$staging = Join-Path $root 'package'
New-Item -ItemType Directory -Force -Path $root,$out,$staging | Out-Null

function Get-LatestPackageVersion([string]$id, [string]$prefix) {
    $index = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/index.json"
    $matches = @($index.versions | Where-Object { $_ -like "$prefix*" })
    if ($matches.Count -eq 0) { throw "No $id package version starts with $prefix" }
    return $matches[-1]
}

try {
    $expectedCommit = '52d86c7480778afb83e476ac742895f73fbf6d7f'
    git clone https://github.com/BOTLANNER/BannerlordPlayerSettlement.git $src 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git clone failed with exit code $LASTEXITCODE" }
    git -C $src checkout $expectedCommit 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git checkout failed with exit code $LASTEXITCODE" }
    $actualCommit = (git -C $src rev-parse HEAD).Trim()
    if ($actualCommit -ne $expectedCommit) { throw "Unexpected upstream commit: $actualCommit" }

    $patcher = Join-Path $env:GITHUB_WORKSPACE '_temp_player_settlement_v753\patch_main.py'
    $mainPath = Join-Path $src 'BannerlordPlayerSettlement\Main.cs'
    python $patcher $mainPath
    if ($LASTEXITCODE -ne 0) { throw "Main.cs patcher failed with exit code $LASTEXITCODE" }

    $coreVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.Core' '1.3.15.'
    $nativeVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.Native' '1.3.15.'
    $sandboxVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.SandBox' '1.3.15.'
    $storyVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.StoryMode' '1.3.15.'
    Write-Host "References: Core=$coreVersion Native=$nativeVersion SandBox=$sandboxVersion StoryMode=$storyVersion"

    $project = Join-Path $src 'BannerlordPlayerSettlement\BannerlordPlayerSettlement.csproj'
    $projectXml = Get-Content -Raw $project
    $projectXml = $projectXml -replace '<Version>7\.5\.0</Version>', '<Version>7.5.3</Version>'
    $projectXml = $projectXml -replace '<GameVersion Condition="\$\(IsStable\) == ''true''">[^<]+</GameVersion>', '<GameVersion Condition="$(IsStable) == ''true''">1.3.15</GameVersion>'
    $projectXml = $projectXml -replace '<GameVersion Condition="\$\(IsBeta\) == ''true''">[^<]+</GameVersion>', '<GameVersion Condition="$(IsBeta) == ''true''">1.3.15</GameVersion>'
    $projectXml = $projectXml -replace '(?s)\s*<ItemGroup>\s*<!-- Bannerlord Native Assemblies -->.*?</ItemGroup>', ''
    $projectXml = $projectXml -replace '(?s)\s*<ItemGroup>\s*<Reference Update="\$\(GameFolder\).*?</ItemGroup>', ''
    $packageRefs = @"
    <PackageReference Include="System.Numerics.Vectors" Version="4.5.0" />
    <PackageReference Include="Bannerlord.UIExtenderEx" Version="2.13.2" IncludeAssets="compile" />
    <PackageReference Include="Bannerlord.ButterLib" Version="2.10.2" IncludeAssets="compile" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.Core" Version="$coreVersion" PrivateAssets="all" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.Native" Version="$nativeVersion" PrivateAssets="all" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.SandBox" Version="$sandboxVersion" PrivateAssets="all" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.StoryMode" Version="$storyVersion" PrivateAssets="all" />
"@
    $projectXml = $projectXml -replace '<PackageReference Include="System\.Numerics\.Vectors" Version="4\.5\.0" />', $packageRefs.TrimEnd()
    $projectXml = $projectXml -replace '(?s)\s*<Target Name="PostBuild".*?</Target>', ''
    Set-Content -Encoding UTF8 $project $projectXml

    git -C $src diff | Set-Content -Encoding UTF8 (Join-Path $out 'source.patch')
    & dotnet build $project -c Beta_Release -p:Platform=x64 --nologo -v:minimal *> (Join-Path $out 'build.log')
    if ($LASTEXITCODE -ne 0) {
        Get-Content (Join-Path $out 'build.log') | Select-Object -Last 180 | ForEach-Object { Write-Host $_ }
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    $built = Get-ChildItem (Join-Path $src 'BannerlordPlayerSettlement\bin') -Recurse -Filter 'PlayerSettlement.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $built) { throw 'PlayerSettlement.dll was not produced' }
    $builtPdb = [System.IO.Path]::ChangeExtension($built.FullName, '.pdb')

    $baseModule = Join-Path $staging 'PlayerSettlement'
    $torModule = Join-Path $staging 'PlayerSettlement_TOR'
    Copy-Item (Join-Path $src 'BannerlordPlayerSettlement\_Module') $baseModule -Recurse -Force

    foreach ($runtime in 'Win64_Shipping_Client','Gaming.Desktop.x64_Shipping_Client') {
        $runtimeDir = Join-Path $baseModule (Join-Path 'bin' $runtime)
        New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
        Copy-Item $built.FullName (Join-Path $runtimeDir 'PlayerSettlement.dll') -Force
        if (Test-Path $builtPdb) { Copy-Item $builtPdb (Join-Path $runtimeDir 'PlayerSettlement.pdb') -Force }
    }

    $baseXmlPath = Join-Path $baseModule 'SubModule.xml'
    $baseXml = Get-Content -Raw $baseXmlPath
    $baseXml = $baseXml -replace '<Version value="v7\.5\.0"/>', '<Version value="v7.5.3"/>'
    $baseXml = $baseXml -replace 'DependentVersion="v1\.3\.9"', 'DependentVersion="v1.3.15"'
    Set-Content -Encoding UTF8 $baseXmlPath $baseXml

    $safeTemplateDir = Join-Path $torModule 'ModuleData\Player_Settlement_Templates'
    New-Item -ItemType Directory -Force -Path $safeTemplateDir | Out-Null
    $baseTemplateDir = Join-Path $baseModule 'ModuleData\Player_Settlement_Templates'
    $mappings = @(
        @{ Target = 'eonir'; Source = 'battania' },
        @{ Target = 'mousillon'; Source = 'vlandia' },
        @{ Target = 'blooddragons'; Source = 'vlandia' },
        @{ Target = 'chaos_culture'; Source = 'sturgia' }
    )
    foreach ($mapping in $mappings) {
        $sourceFile = Join-Path $baseTemplateDir ($mapping.Source + '_settlements_templates_default.xml')
        $targetFile = Join-Path $safeTemplateDir ($mapping.Target + '_settlements_templates_resource_safe.xml')
        $template = Get-Content -Raw $sourceFile
        $oldCulture = 'culture_template="' + $mapping.Source + '"'
        $newCulture = 'culture_template="' + $mapping.Target + '"'
        if (-not $template.Contains($oldCulture)) { throw "Culture marker missing in $sourceFile" }
        $template = $template.Replace($oldCulture, $newCulture)
        Set-Content -Encoding UTF8 $targetFile $template
    }

    $torSubModule = @'
<Module>
  <Name value="Player Settlement: The Old Realms (1.16 resource-safe)"/>
  <Id value="PlayerSettlement_TOR"/>
  <Version value="v1.2.0"/>
  <Url value="https://www.nexusmods.com/mountandblade2bannerlord/mods/7298" />
  <SingleplayerModule value="true"/>
  <MultiplayerModule value="false"/>
  <Official value="false"/>
  <DefaultModule value="false" />
  <ModuleCategory value="Singleplayer" />
  <ModuleType value="Community" />
  <UpdateInfo value="NexusMods:7298" />
  <PlayerSettlementsTemplates path="ModuleData/Player_Settlement_Templates" />
  <DependedModules>
    <DependedModule Id="Bannerlord.Harmony" DependentVersion="v2.4.2" />
    <DependedModule Id="Bannerlord.ButterLib" DependentVersion="v2.10.2" />
    <DependedModule Id="Bannerlord.UIExtenderEx" DependentVersion="v2.13.2" />
    <DependedModule Id="Bannerlord.MBOptionScreen" DependentVersion="v5.11.3" />
    <DependedModule Id="Native" DependentVersion="v1.3.15" />
    <DependedModule Id="SandBoxCore" DependentVersion="v1.3.15" />
    <DependedModule Id="Sandbox" DependentVersion="v1.3.15" />
    <DependedModule Id="StoryMode" DependentVersion="v1.3.15" />
    <DependedModule Id="TOR_Armory" />
    <DependedModule Id="TOR_Environment" />
    <DependedModule Id="TOR_Core" />
    <DependedModule Id="PlayerSettlement" DependentVersion="v7.5.3" />
  </DependedModules>
  <DependedModuleMetadatas>
    <DependedModuleMetadata id="Bannerlord.Harmony" order="LoadBeforeThis" version="v2.4.2" />
    <DependedModuleMetadata id="Bannerlord.ButterLib" order="LoadBeforeThis" version="v2.10.2" />
    <DependedModuleMetadata id="Bannerlord.UIExtenderEx" order="LoadBeforeThis" version="v2.13.2" />
    <DependedModuleMetadata id="Bannerlord.MBOptionScreen" order="LoadBeforeThis" version="v5.11.3" />
    <DependedModuleMetadata id="Native" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="SandBoxCore" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="Sandbox" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="StoryMode" order="LoadBeforeThis" version="1.0.0.*" />
    <DependedModuleMetadata id="TOR_Armory" order="LoadBeforeThis" version="v1.16.0" />
    <DependedModuleMetadata id="TOR_Environment" order="LoadBeforeThis" version="v1.16.0" />
    <DependedModuleMetadata id="TOR_Core" order="LoadBeforeThis" version="v1.16.0" />
    <DependedModuleMetadata id="PlayerSettlement" order="LoadBeforeThis" version="v7.5.3" />
  </DependedModuleMetadatas>
  <SubModules />
  <Xmls />
</Module>
'@
    Set-Content -Encoding UTF8 (Join-Path $torModule 'SubModule.xml') $torSubModule

    $notes = @'
Player Settlement 7.5.3 - Bannerlord 1.3.15 / The Old Realms WiTM 1.16

IMPORTANT INSTALLATION REQUIREMENT
Delete the old Modules\PlayerSettlement and Modules\PlayerSettlement_TOR folders before extracting this package.
Do not merge this package over 7.5.1 or 7.5.2. Old PlayerSettlement_TOR\Prefabs files must not remain.

Startup isolation changes
- No compatibility discovery, Harmony patching, UIExtender registration, settings initialization, template parsing, or hotkey construction runs during Bannerlord's initial module startup.
- Managed runtime initialization is deferred until a campaign game starts.
- Harmony patches are installed one class at a time. A failed managed patch no longer aborts all remaining patches.
- A raw startup trace is written to Documents\Mount and Blade II Bannerlord\Configs\BannerlordPlayerSettlement\startup.log.

The Old Realms resource safety changes
- Removed the old ToR-specific Prefabs directory authored for ToR 1.2.11. Those resources were being loaded by the engine before managed exception handling and were not validated for ToR 1.16.
- ToR 1.16 cultures use safe fallback visual/template sets backed by the base Player Settlement prefabs:
  eonir -> battania
  mousillon -> vlandia
  blooddragons -> vlandia
  chaos_culture -> sturgia
- battania and khuzait already use the base module's matching culture templates.
- This intentionally trades old ToR-specific settlement visuals for startup safety and functional placement.
'@
    Set-Content -Encoding UTF8 (Join-Path $staging 'COMPATIBILITY_NOTES.txt') $notes
    Set-Content -Encoding UTF8 (Join-Path $staging 'DELETE_OLD_MODULE_FOLDERS_FIRST.txt') 'Delete Modules\PlayerSettlement and Modules\PlayerSettlement_TOR before installing. Old ToR prefab files must not remain.'

    if (Test-Path (Join-Path $torModule 'Prefabs')) { throw 'Resource-safe ToR module unexpectedly contains a Prefabs directory' }

    $xmlFiles = Get-ChildItem $staging -Recurse -Filter '*.xml'
    foreach ($xmlFile in $xmlFiles) {
        try { [xml](Get-Content -Raw $xmlFile.FullName) | Out-Null }
        catch { throw "Invalid XML: $($xmlFile.FullName): $($_.Exception.Message)" }
    }

    $runtimeDlls = Get-ChildItem (Join-Path $baseModule 'bin') -Recurse -Filter 'PlayerSettlement.dll'
    if ($runtimeDlls.Count -ne 2) { throw "Expected two runtime DLLs; found $($runtimeDlls.Count)" }
    $hashes = @($runtimeDlls | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash })
    if (($hashes | Select-Object -Unique).Count -ne 1) { throw 'Runtime DLL copies are not identical' }

    $zip = Join-Path $out 'Player_Settlements_7.5.3_BL_1.3.15_ToR_WiTM_1.16_ResourceSafe.zip'
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
    Get-FileHash $zip -Algorithm SHA256 | Format-List | Out-String | Set-Content -Encoding UTF8 (Join-Path $out 'SHA256.txt')
    Get-ChildItem $staging -Recurse -File | ForEach-Object { $_.FullName.Substring($staging.Length + 1) } | Sort-Object | Set-Content -Encoding UTF8 (Join-Path $out 'package_manifest.txt')
    Copy-Item $built.FullName (Join-Path $out 'PlayerSettlement.dll') -Force
    if (Test-Path $builtPdb) { Copy-Item $builtPdb (Join-Path $out 'PlayerSettlement.pdb') -Force }

    Write-Host "Built package: $zip"
    Write-Host "Parsed XML files: $($xmlFiles.Count)"
    Write-Host "PlayerSettlement.dll SHA256: $($hashes[0])"
}
catch {
    Write-Host "FATAL: $($_.Exception.Message)"
    ($_ | Out-String) | Set-Content -Encoding UTF8 (Join-Path $out 'fatal.log')
    throw
}
