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
            var hasClassAttribute = type.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0;
            var hasMethodAttribute = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Any(method => method.GetCustomAttributes(typeof(HarmonyPatch), false).Length != 0);
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
                    Console.WriteLine("OK " + type.FullName + " => " + string.Join(", ", patched.Select(x => x.DeclaringType.FullName + "." + x.Name)));
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
$validationLog = Join-Path $OutputDirectory 'patch-validation.log'
& $exe.FullName (Resolve-Path $PlayerSettlementDll).Path *> $validationLog
$exitCode = $LASTEXITCODE
Get-Content $validationLog | Select-Object -Last 120 | ForEach-Object { Write-Host $_ }
if ($exitCode -ne 0) { throw "Harmony patch-target validation failed with exit code $exitCode" }
