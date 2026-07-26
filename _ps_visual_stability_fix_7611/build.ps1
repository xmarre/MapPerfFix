$ErrorActionPreference = 'Stop'

$workspace = $env:GITHUB_WORKSPACE
$baseBuild = Join-Path $workspace '_ps_visual_startup_fix_769/build.ps1'
$baseInput = Join-Path $workspace '_ps_visual_startup_fix_769/input'
$input = Join-Path $workspace '_ps_visual_stability_fix_7611/input'
$helper = Join-Path $input 'CultureVisualHelper.cs'
$levelPatch = Join-Path $input 'patch_level_visibility_7611.py'
$visualPatch = Join-Path $baseInput 'patch_visual_startup.py'
$generatedBuild = Join-Path $workspace '_ps_visual_stability_fix_7611/generated_build_7611.ps1'

if (-not (Test-Path $baseBuild)) { throw 'Validated 7.6.9 build script is missing' }
if (-not (Test-Path $helper)) { throw '7.6.11 CultureVisualHelper.cs is missing' }
if (-not (Test-Path $levelPatch)) { throw '7.6.11 level visibility patch is missing' }
if (-not (Test-Path $visualPatch)) { throw 'Inherited 7.6.9 visual startup patch is missing' }

$expectedHelperHash = '4edb2682d63f9f897640f306c4fdfa5b864b6a6fb1948e9c831baa443be6d4ce'
$expectedLevelPatchHash = 'c44f68dcf9730d14bb49573d9c459e50ac56a338297004bd0ed8e23f15ce75aa'
$expectedVisualPatchHash = '161a3f42ee089b8313fc7df71b6f66e118e12a59c8457e6d1e94a57ae6f2e89f'

$actualHelperHash = (Get-FileHash $helper -Algorithm SHA256).Hash.ToLowerInvariant()
$actualLevelPatchHash = (Get-FileHash $levelPatch -Algorithm SHA256).Hash.ToLowerInvariant()
$actualVisualPatchHash = (Get-FileHash $visualPatch -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHelperHash -ne $expectedHelperHash) { throw "CultureVisualHelper.cs SHA-256 mismatch. Expected=$expectedHelperHash Actual=$actualHelperHash" }
if ($actualLevelPatchHash -ne $expectedLevelPatchHash) { throw "patch_level_visibility_7611.py SHA-256 mismatch. Expected=$expectedLevelPatchHash Actual=$actualLevelPatchHash" }
if ($actualVisualPatchHash -ne $expectedVisualPatchHash) { throw "Inherited patch_visual_startup.py SHA-256 mismatch. Expected=$expectedVisualPatchHash Actual=$actualVisualPatchHash" }
Write-Host "Verified 7.6.11 helper SHA-256: $actualHelperHash"
Write-Host "Verified 7.6.11 level patch SHA-256: $actualLevelPatchHash"
Write-Host "Verified inherited visual patch SHA-256: $actualVisualPatchHash"

Copy-Item $helper (Join-Path $baseInput 'CultureVisualHelper.cs') -Force

$script = Get-Content -Raw $baseBuild
$oldHelperHash = "'CultureVisualHelper.cs' = '665df6b9d9188b94131f108585e9d544d2ffd495107815cd35f507965e738a1d'"
$newHelperHash = "'CultureVisualHelper.cs' = '$actualHelperHash'"
if (-not $script.Contains($oldHelperHash)) { throw 'Inherited helper hash marker not found' }
$script = $script.Replace($oldHelperHash, $newHelperHash)

$oldVisualHash = "'patch_visual_startup.py' = '89b9ed95c39b0873a5716602751c4e0a98d101a14368587efb85ab404ef92125'"
$newVisualHash = "'patch_visual_startup.py' = '$actualVisualPatchHash'"
if (-not $script.Contains($oldVisualHash)) { throw 'Inherited visual patch hash marker not found' }
$script = $script.Replace($oldVisualHash, $newVisualHash)

$applyMarker = 'if ($LASTEXITCODE -ne 0) { throw "7.6.9 visual startup patch failed: $LASTEXITCODE" }'
if (-not $script.Contains($applyMarker)) { throw 'Visual patch insertion marker not found' }
$applyInsertion = $applyMarker + "`n" +
    "python (Join-Path `$workspace '_ps_visual_stability_fix_7611/input/patch_level_visibility_7611.py') `$partyVisualPath`n" +
    'if ($LASTEXITCODE -ne 0) { throw "7.6.11 settlement level visibility patch failed: $LASTEXITCODE" }'
$script = $script.Replace($applyMarker, $applyInsertion)

$validationMarker = '$partyText = Get-Content -Raw $partyVisualPath'
if (-not $script.Contains($validationMarker)) { throw 'Validation insertion marker not found' }
$validationInsertion = $validationMarker + "`n" + @'
$helperText = Get-Content -Raw (Join-Path $patchDir 'CultureVisualHelper.cs')
if ($helperText.Contains('GameEntity.CopyFromPrefab(')) { throw 'Unsafe CopyFromPrefab live-entity path remains' }
if (-not $helperText.Contains('GameEntity.Instantiate(')) { throw 'Independent prefab instantiation path missing' }
if (-not $helperText.Contains('GameEntity.CopyFrom(')) { throw 'Scene-instance fallback clone path missing' }
if (-not $partyText.Contains('[HarmonyPatch("SetSettlementLevelVisibility")]')) { throw 'ToR level visibility bypass missing' }
if (-not $partyText.Contains('CultureVisualHelper.IsTorPlayerSettlement(settlement)')) { throw 'ToR-only level visibility scope missing' }
$bannerRemovalPattern = 'mapInteractionEntities\.Add\(gameEntity\);\s*\}\s*____gateBannerEntitiesWithLevels = bannerEntitiesByLevel;'
if (-not [regex]::IsMatch($partyText, $bannerRemovalPattern)) { throw 'Native banner-placeholder removal lifecycle was not restored' }
'@
$script = $script.Replace($validationMarker, $validationInsertion)

$script = $script.Replace('7.6.9', '7.6.11')
$script = $script.Replace('VisualStartupFix', 'VisualPhysicsFix')
$script = $script.Replace('visual startup', 'visual physics')
$script = $script.Replace('Visual startup', 'Visual physics')
$script = $script.Replace(
    'Fix=SettlementVisual.OnStartup null-safety; created ToR visual fallback; exact-culture preview; ownership lifecycle; integrated culture helper',
    'Fix=independent ToR prefab instantiation; custom level-visibility physics bypass; visual startup null-safety; raid and siege ownership lifecycle')

Set-Content -Encoding UTF8 $generatedBuild $script
& $generatedBuild
if ($LASTEXITCODE -ne 0) { throw "Generated 7.6.11 build failed: $LASTEXITCODE" }
