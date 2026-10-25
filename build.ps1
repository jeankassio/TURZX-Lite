param([switch]$Run)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\TurzxLite\TurzxLite.csproj'
$destination = Join-Path $PSScriptRoot 'dist\TURZX Lite'
& dotnet publish $project -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'A compilação falhou.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LEIA-ME.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-PTBR.md') -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination $destination -Force
Write-Host ('Pronto: ' + (Join-Path $destination 'TURZX Lite.exe'))
if ($Run) { Start-Process -FilePath (Join-Path $destination 'TURZX Lite.exe') -WindowStyle Hidden }
