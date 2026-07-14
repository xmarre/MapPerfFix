param(
    [Parameter(Mandatory = $true)][string]$PlayerSettlementDll,
    [Parameter(Mandatory = $true)][string]$ReferenceVersionsFile,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$versions = @{}
Get-Content $ReferenceVersionsFile | ForEach-Object {
    if ($_ -match '^([^=]+)=(.+)$') { $versions[$matches[1]] = $matches[2] }
}
foreach ($key in 'Core','Native','SandBox','StoryMode') {
    if (-not $versions.ContainsKey($key)) { throw "Missing reference version $key" }
}

$projectDir = Join-Path $OutputDirectory 'patch-validator'
New-Item -ItemType Directory -Force -Path $projectDir | Out-Null
$dllPath = [System.Security.SecurityElement]::Escape((Resolve-Path $PlayerSettlementDll).Path)
$project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="PlayerSettlement">
      <HintPath>$dllPath</HintPath>
      <Private>true</Private>
    </Reference>
    <PackageReference Include="Lib.Harmony" Version="2.4.2" />
    <PackageReference Include="Bannerlord.MCM" Version="5.11.3" />
    <PackageReference Include="Bannerlord.UIExtenderEx" Version="2.13.2" />
    <PackageReference Include="Bannerlord.ButterLib" Version="2.10.2" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.Core" Version="$($versions.Core)" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.Native" Version="$($versions.Native)" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.SandBox" Version="$($versions.SandBox)" />
    <PackageReference Include="Bannerlord.ReferenceAssemblies.StoryMode" Version="$($versions.StoryMode)" />
  </ItemGroup>
</Project>
"@
$projectPath = Join-Path $projectDir 'PatchValidator.csproj'
Set-Content -Encoding UTF8 $projectPath $project

$program = @'
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Expected PlayerSettlement.dll path.");
            return 2;
        }

        var assembly = Assembly.LoadFrom(args[0]);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loaderException in ex.LoaderExceptions.Where(x => x != null))
                Console.WriteLine("LOADER_FAIL " + loaderException);
            types = ex.Types.Where(x => x != null).ToArray();
        }

        var harmony = new Harmony("player.settlement.compatibility.validator");
        var failures = new List<string>();
        var tested = 0;
        var skipped = 0;

        foreach (var type in types.OrderBy(x => x.FullName, StringComparer.Ordinal))
        {
            bool hasClassAttribute;
            bool hasMethodAttribute;
            try
            {
                hasClassAttribute = type.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0;
                hasMethodAttribute = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .Any(method => method.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0);
            }
            catch (Exception ex)
            {
                failures.Add("FAIL_ATTRIBUTE " + type.FullName + " => " + ex);
                Console.WriteLine(failures[failures.Count - 1]);
                continue;
            }
            if (!hasClassAttribute && !hasMethodAttribute)
                continue;

            tested++;
            try
            {
                var patched = harmony.CreateClassProcessor(type).Patch();
                if (patched == null || patched.Count == 0)
                {
                    skipped++;
                    Console.WriteLine("SKIP " + type.FullName);
                }
                else
                {
                    var targets = patched.Select(method => method == null
                        ? "<null-placeholder>"
                        : (method.DeclaringType == null ? "<unknown-type>" : method.DeclaringType.FullName) + "." + method.Name);
                    Console.WriteLine("OK " + type.FullName + " => " + string.Join(", ", targets));
                }
            }
            catch (Exception ex)
            {
                var message = "FAIL " + type.FullName + " => " + ex;
                failures.Add(message);
                Console.WriteLine(message);
            }
        }

        try { harmony.UnpatchAll(harmony.Id); } catch { }
        Console.WriteLine($"SUMMARY tested={tested} skipped={skipped} failed={failures.Count}");
        return failures.Count == 0 ? 0 : 1;
    }
}
'@
Set-Content -Encoding UTF8 (Join-Path $projectDir 'Program.cs') $program

$buildLog = Join-Path $OutputDirectory 'patch-validation-build.log'
& dotnet build $projectPath -c Release --nologo -v:minimal *> $buildLog
if ($LASTEXITCODE -ne 0) {
    Get-Content $buildLog | Select-Object -Last 80 | ForEach-Object { Write-Host $_ }
    throw "Patch validator failed to compile"
}

$exe = Get-ChildItem (Join-Path $projectDir 'bin\Release') -Recurse -Filter 'PatchValidator.exe' | Select-Object -First 1
if (-not $exe) { throw 'PatchValidator.exe was not produced' }
$runtimeDir = $exe.Directory.FullName
$nugetRoot = Join-Path $env:USERPROFILE '.nuget\packages'
$copySpecs = @(
    @{ Id = 'bannerlord.referenceassemblies.core'; Version = $versions.Core },
    @{ Id = 'bannerlord.referenceassemblies.native'; Version = $versions.Native },
    @{ Id = 'bannerlord.referenceassemblies.sandbox'; Version = $versions.SandBox },
    @{ Id = 'bannerlord.referenceassemblies.storymode'; Version = $versions.StoryMode }
)
$copied = 0
foreach ($spec in $copySpecs) {
    $refDir = Join-Path $nugetRoot ("$($spec.Id)\$($spec.Version)\ref\net472")
    if (-not (Test-Path $refDir)) { throw "Reference directory not found: $refDir" }
    $dlls = @(Get-ChildItem $refDir -File -Filter '*.dll')
    if ($dlls.Count -eq 0) { throw "No reference DLLs found: $refDir" }
    foreach ($dll in $dlls) {
        Copy-Item $dll.FullName (Join-Path $runtimeDir $dll.Name) -Force
        $copied++
    }
}
Write-Host "Copied $copied Bannerlord reference DLLs into validator runtime."
Copy-Item (Resolve-Path $PlayerSettlementDll).Path (Join-Path $runtimeDir 'PlayerSettlement.dll') -Force

$validationLog = Join-Path $OutputDirectory 'patch-validation.log'
& $exe.FullName (Join-Path $runtimeDir 'PlayerSettlement.dll') *> $validationLog
$exitCode = $LASTEXITCODE
Get-Content $validationLog | Select-Object -Last 200 | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) { throw "Harmony patch-target validation failed with exit code $exitCode" }
