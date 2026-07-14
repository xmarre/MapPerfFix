$ErrorActionPreference = 'Stop'

$root = Join-Path $env:GITHUB_WORKSPACE '_player_settlement_startup_fix'
$src = Join-Path $root 'source'
$out = Join-Path $root 'out'
$staging = $null
New-Item -ItemType Directory -Force -Path $root,$out | Out-Null

function Get-LatestPackageVersion([string]$id, [string]$prefix) {
    $index = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($id.ToLowerInvariant())/index.json"
    $matches = @($index.versions | Where-Object { $_ -like "$prefix*" })
    if ($matches.Count -eq 0) { throw "No $id package version starts with $prefix" }
    return $matches[-1]
}

try {
    $expectedCommit = '52d86c7480778afb83e476ac742895f73fbf6d7f'
    git clone --depth=1 https://github.com/BOTLANNER/BannerlordPlayerSettlement.git $src 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "git clone failed with exit code $LASTEXITCODE" }
    $actualCommit = (git -C $src rev-parse HEAD).Trim()
    if ($actualCommit -ne $expectedCommit) { throw "Unexpected upstream HEAD: $actualCommit (expected $expectedCommit)" }

    $mainPath = Join-Path $src 'BannerlordPlayerSettlement\Main.cs'
    $main = Get-Content -Raw $mainPath

    $oldField = '        private static List<ICompatibilityPatch> HarmonyCompatPatches = LoadCompatPatches().ToList();'
    $newField = @'
        private static readonly List<ICompatibilityPatch> HarmonyCompatPatches = new();
        private static bool HarmonyCompatPatchesLoaded;
'@.TrimEnd()
    if (-not $main.Contains($oldField)) { throw 'Compatibility patch static initializer was not found' }
    $main = $main.Replace($oldField, $newField)

    $oldStartup = '                LogManager.EnableTracer = true; // enable code event tracing'
    $newStartup = @'
                LogManager.EnableTracer = true; // enable code event tracing

                if (!HarmonyCompatPatchesLoaded)
                {
                    HarmonyCompatPatches.AddRange(LoadCompatPatches());
                    HarmonyCompatPatchesLoaded = true;
                }
'@.TrimEnd()
    if (-not $main.Contains($oldStartup)) { throw 'OnSubModuleLoad insertion point was not found' }
    $main = $main.Replace($oldStartup, $newStartup)

    $oldLoader = @'
        static IEnumerable<ICompatibilityPatch> LoadCompatPatches()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    if (typeof(ICompatibilityPatch).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                    {
                        object? inst = null;
                        try
                        {
                            inst = type.CreateInstance();
                        }
                        catch (Exception e)
                        {
                            LogManager.Log.NotifyBad(e);
                        }

                        if (inst is ICompatibilityPatch compatibilityPatch)
                        {
                            yield return compatibilityPatch;
                        }

                    }
                }
            }
        }
'@
    $newLoader = @'
        static IEnumerable<ICompatibilityPatch> LoadCompatPatches()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(type => type != null).Cast<Type>().ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (typeof(ICompatibilityPatch).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
                    {
                        object? inst = null;
                        try
                        {
                            inst = type.CreateInstance();
                        }
                        catch (Exception e)
                        {
                            LogManager.Log.NotifyBad(e);
                        }

                        if (inst is ICompatibilityPatch compatibilityPatch)
                        {
                            yield return compatibilityPatch;
                        }
                    }
                }
            }
        }
'@
    if (-not $main.Contains($oldLoader)) { throw 'Compatibility loader method was not found' }
    $main = $main.Replace($oldLoader, $newLoader)
    Set-Content -Encoding UTF8 $mainPath $main

    $coreVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.Core' '1.3.15.'
    $nativeVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.Native' '1.3.15.'
    $sandboxVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.SandBox' '1.3.15.'
    $storyVersion = Get-LatestPackageVersion 'Bannerlord.ReferenceAssemblies.StoryMode' '1.3.15.'
    Write-Host "References: Core=$coreVersion Native=$nativeVersion SandBox=$sandboxVersion StoryMode=$storyVersion"

    $project = Join-Path $src 'BannerlordPlayerSettlement\BannerlordPlayerSettlement.csproj'
    $xml = Get-Content -Raw $project
    $xml = $xml -replace '<Version>7\.5\.0</Version>', '<Version>7.5.2</Version>'
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
    & dotnet build $project -c Beta_Release -p:Platform=x64 --nologo -v:minimal *> (Join-Path $out 'build.log')
    if ($LASTEXITCODE -ne 0) {
        Get-Content (Join-Path $out 'build.log') | Select-Object -Last 120 | ForEach-Object { Write-Host $_ }
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    $built = Get-ChildItem (Join-Path $src 'BannerlordPlayerSettlement\bin') -Recurse -Filter 'PlayerSettlement.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $built) { throw 'PlayerSettlement.dll was not produced' }
    $builtPdb = [System.IO.Path]::ChangeExtension($built.FullName, '.pdb')

    $staging = Join-Path $root 'package'
    $baseModule = Join-Path $staging 'PlayerSettlement'
    $torModule = Join-Path $staging 'PlayerSettlement_TOR'
    Copy-Item (Join-Path $src 'BannerlordPlayerSettlement\_Module') $baseModule -Recurse -Force
    Copy-Item (Join-Path $src 'PlayerSettlement_TOR') $torModule -Recurse -Force

    foreach ($runtime in 'Win64_Shipping_Client','Gaming.Desktop.x64_Shipping_Client') {
        $runtimeDir = Join-Path $baseModule (Join-Path 'bin' $runtime)
        New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
        Copy-Item $built.FullName (Join-Path $runtimeDir 'PlayerSettlement.dll') -Force
        if (Test-Path $builtPdb) { Copy-Item $builtPdb (Join-Path $runtimeDir 'PlayerSettlement.pdb') -Force }
    }

    $baseXmlPath = Join-Path $baseModule 'SubModule.xml'
    $baseXml = Get-Content -Raw $baseXmlPath
    $baseXml = $baseXml -replace '<Version value="v7\.5\.0"/>', '<Version value="v7.5.2"/>'
    $baseXml = $baseXml -replace 'DependentVersion="v1\.3\.9"', 'DependentVersion="v1.3.15"'
    Set-Content -Encoding UTF8 $baseXmlPath $baseXml

    $torXmlPath = Join-Path $torModule 'SubModule.xml'
    $torXml = Get-Content -Raw $torXmlPath
    $torXml = $torXml -replace '<Version value="v1\.0\.0"/>', '<Version value="v1.1.1"/>'
    $torXml = $torXml -replace 'DependentVersion="v1\.3\.9"', 'DependentVersion="v1.3.15"'
    $torXml = $torXml -replace '<DependedModule Id="PlayerSettlement" />', '<DependedModule Id="PlayerSettlement" DependentVersion="v7.5.2" />'
    $torXml = $torXml -replace 'version="v1\.2\.11"', 'version="v1.16.0"'
    $torXml = $torXml -replace 'version="v6\.0\.0"', 'version="v7.5.2"'
    Set-Content -Encoding UTF8 $torXmlPath $torXml

    $notes = @'
Player Settlement 7.5.2 startup fix

Targets:
- Mount & Blade II: Bannerlord 1.3.15
- The Old Realms: War in the Mountains 1.16

Startup fix:
- Removed compatibility discovery from Main's static type initializer.
- Compatibility extensions are now discovered inside the guarded OnSubModuleLoad path.
- ReflectionTypeLoadException is handled per loaded assembly, preserving loadable types and skipping assemblies that cannot be enumerated.

Install:
1. Delete existing PlayerSettlement and PlayerSettlement_TOR module folders.
2. Extract both folders into Bannerlord/Modules.
3. Load PlayerSettlement before PlayerSettlement_TOR.
'@
    Set-Content -Encoding UTF8 (Join-Path $staging 'COMPATIBILITY_NOTES.txt') $notes

    $zip = Join-Path $out 'Player_Settlements_7.5.2_BL_1.3.15_ToR_WiTM_1.16_StartupFix.zip'
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force
    Copy-Item $built.FullName (Join-Path $out 'PlayerSettlement.dll') -Force
    if (Test-Path $builtPdb) { Copy-Item $builtPdb (Join-Path $out 'PlayerSettlement.pdb') -Force }
    Get-FileHash $zip -Algorithm SHA256 | Format-List | Out-String | Set-Content -Encoding UTF8 (Join-Path $out 'SHA256.txt')
    Write-Host "Built package: $zip"
}
catch {
    Write-Host "FATAL: $($_.Exception.Message)"
    ($_ | Out-String) | Set-Content -Encoding UTF8 (Join-Path $out 'fatal.log')
    throw
}
