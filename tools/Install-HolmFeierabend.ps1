# Builds a self-contained Holm with Gleitzeit into this user's local app folder
# and adds a Start Menu shortcut. Does not replace an existing Holm install.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA "HolmFeierabend"
$publish = Join-Path $root "out\holm-feierabend"

Write-Host "Baue Holm mit Gleitzeit …"
dotnet publish (Join-Path $root "Holm.App\Holm.App.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
if ($LASTEXITCODE -ne 0) { throw "Der Build ist fehlgeschlagen." }

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $publish "Holm.App.exe") (Join-Path $dest "Holm.exe") -Force
$exe = Join-Path $dest "Holm.exe"

$shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "Holm Feierabend.lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.Description = "Holm mit Gleitzeit aus dem BBC-Extranet"
$link.Save()

Write-Host "Fertig: $exe"
Write-Host "Startmenü: Holm Feierabend"
