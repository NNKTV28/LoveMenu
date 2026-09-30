# Builds the plugin and the launcher that embeds it.
#   .\build.ps1        public release (developer tools hidden) -> LoveMenuLauncher.exe
#   .\build.ps1 -Dev   dev build (developer tools shown)       -> LoveMenu-dev.exe
# Always a full rebuild, so a dev plugin never ends up inside a release exe.
param([switch]$Dev)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$devFlag = if ($Dev) { 'true' } else { 'false' }

dotnet build "$root\FlyMod.csproj" -c Release --no-incremental -nologo -v q "-p:DevBuild=$devFlag"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed' }
dotnet build "$root\Launcher\Launcher.csproj" -c Release --no-incremental -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Launcher build failed' }

$out = "$root\Launcher\bin\Release\net472"
if ($Dev) {
    Copy-Item "$out\LoveMenuLauncher.exe" "$out\LoveMenu-dev.exe" -Force
    Write-Host "Dev build: $out\LoveMenu-dev.exe"
} else {
    Write-Host "Release build: $out\LoveMenuLauncher.exe"
}
