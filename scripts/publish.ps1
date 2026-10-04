# Builds the downloadable app: one self-contained SWISH.exe (no .NET install needed) plus a README, zipped.
#   .\scripts\publish.ps1
# Output:
#   release\SWISH\SWISH.exe + README.md       ready to run
#   release\SWISH-win-x64.zip                 the same, zipped: attach it to a GitHub Release
#                                             (don't commit it: the exe is over GitHub's 100 MB file limit)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = "$root\release\SWISH"
$zip = "$root\release\SWISH-win-x64.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# Settings live in src\SWISH.App\Properties\PublishProfiles\FolderProfile.pubxml (single file, win-x64, self-contained).
dotnet publish "$root\src\SWISH.App\SWISH.App.csproj" -p:PublishProfile=FolderProfile -p:PublishDir="$out\" --nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Copy-Item "$root\docs\QUICKSTART.md" "$out\README.md"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB)
Write-Host "Built $out\SWISH.exe and $zip ($size MB)."
