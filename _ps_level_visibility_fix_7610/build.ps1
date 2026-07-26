$ErrorActionPreference = 'Stop'

$workspace = $env:GITHUB_WORKSPACE
$baseBuild = Join-Path $workspace '_ps_visual_startup_fix_769/build.ps1'
$inputDir = Join-Path $workspace '_ps_visual_startup_fix_769/input'
$visualPatch = Join-Path $inputDir 'patch_visual_startup.py'
$levelPatch = Join-Path $inputDir 'patch_level_visibility_7610.py'
$generatedBuild = Join-Path $workspace '_ps_level_visibility_fix_7610/generated_build_7610.ps1'

if (-not (Test-Path $baseBuild)) { throw 'Validated 7.6.9 build script is missing' }
if (-not (Test-Path $visualPatch)) { throw 'Validated 7.6.9 visual patch is missing' }
if (-not (Test-Path $levelPatch)) { throw '7.6.10 level-visibility patch is missing' }

$expectedVisualPatchHash = '161a3f42ee089b8313fc7df71b6f66e118e12a59c8457e6d1e94a57ae6f2e89f'
$actualVisualPatchHash = (Get-FileHash $visualPatch -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualVisualPatchHash -ne $expectedVisualPatchHash) {
    throw "7.6.9 visual patch SHA-256 mismatch. Expected=$expectedVisualPatchHash Actual=$actualVisualPatchHash"
}
Write-Host "Verified 7.6.9 visual patch SHA-256: $actualVisualPatchHash"

$expectedPatchHash = 'f2cac790f29bb5a8d46bbe4b5d787df512ce536b58a5237dae54a37ee81251d8'
$actualPatchHash = (Get-FileHash $levelPatch -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualPatchHash -ne $expectedPatchHash) {
    throw "7.6.10 source patch SHA-256 mismatch. Expected=$expectedPatchHash Actual=$actualPatchHash"
}
Write-Host "Verified 7.6.10 source patch SHA-256: $actualPatchHash"

$script = Get-Content -Raw $baseBuild
$oldVisualHash = "'patch_visual_startup.py' = '89b9ed95c39b0873a5716602751c4e0a98d101a14368587efb85ab404ef92125'"
$newVisualHash = "'patch_visual_startup.py' = '$actualVisualPatchHash'"
if (-not $script.Contains($oldVisualHash)) { throw 'Inherited visual patch hash marker not found' }
$script = $script.Replace($oldVisualHash, $newVisualHash)

$applyMarker = 'if ($LASTEXITCODE -ne 0) { throw "7.6.9 visual startup patch failed: $LASTEXITCODE" }'
if (-not $script.Contains($applyMarker)) { throw 'Visual patch insertion marker not found' }
$applyInsertion = $applyMarker + "`n" +
    "python (Join-Path `$input 'patch_level_visibility_7610.py') `$partyVisualPath`n" +
    'if ($LASTEXITCODE -ne 0) { throw "7.6.10 settlement level visibility patch failed: $LASTEXITCODE" }'
$script = $script.Replace($applyMarker, $applyInsertion)

$validationMarker = '$partyText = Get-Content -Raw $partyVisualPath'
if (-not $script.Contains($validationMarker)) { throw 'Validation insertion marker not found' }
$validationInsertion = $validationMarker + "`n" + @'
if (-not $partyText.Contains('Keep banner placeholders alive. SettlementVisual.SetSettlementLevelVisibility')) {
    throw '7.6.10 level-visibility lifetime guard is missing'
}
$bannerRemovalPattern = 'mapInteractionEntities\.Add\(gameEntity\);\s*\}\s*____gateBannerEntitiesWithLevels = bannerEntitiesByLevel;'
if ([regex]::IsMatch($partyText, $bannerRemovalPattern)) {
    throw 'Banner placeholders are still scheduled for native removal'
}
'@
$script = $script.Replace($validationMarker, $validationInsertion)

$script = $script.Replace('7.6.9', '7.6.10')
$script = $script.Replace('VisualStartupFix', 'LevelVisibilityFix')
$script = $script.Replace('visual startup', 'level visibility')
$script = $script.Replace('Visual startup', 'Level visibility')

Set-Content -Encoding UTF8 $generatedBuild $script
& $generatedBuild
if ($LASTEXITCODE -ne 0) { throw "Generated 7.6.10 build failed: $LASTEXITCODE" }
