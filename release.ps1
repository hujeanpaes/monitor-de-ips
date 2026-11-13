<#
.SYNOPSIS
  Genera los archivos de una version de Monitor de IPs y, opcionalmente, la publica en GitHub Releases.

.DESCRIPTION
  La version se lee de src\Program.cs (const string Version). Genera en dist\release\<version>\:
    MonitorIPs-<version>-Setup.exe      instalador (Inno Setup)
    MonitorIPs-<version>-Portable.exe   ejecutable portable
    MonitorIPs-<version>-Portable.zip   portable + LEEME.txt
    SHA256SUMS.txt                      huellas SHA-256
  Con -Publish ademas crea la etiqueta v<version> y la Release en GitHub, con las notas de la
  seccion correspondiente de CHANGELOG.md.

.EXAMPLE
  .\release.ps1              # solo genera los archivos
.EXAMPLE
  .\release.ps1 -Publish     # genera, etiqueta y publica (desde main, sin cambios pendientes)
#>
[CmdletBinding()]
param([switch]$Publish)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
$utf8Bom   = New-Object System.Text.UTF8Encoding $true

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- version y herramientas
$match = Select-String -Path 'src\Program.cs' -Pattern 'const string Version = "(\d+\.\d+\.\d+)"'
if (-not $match) { throw 'No se encontro la version en src\Program.cs' }
$version = $match.Matches[0].Groups[1].Value
$tag = "v$version"

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'No se encontro csc.exe de .NET Framework 4' }

$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Falta Inno Setup 6. Instalalo con: winget install JRSoftware.InnoSetup' }

if ($Publish) {
    Step 'Comprobando el repositorio'
    if ((git rev-parse --abbrev-ref HEAD) -ne 'main') { throw 'Publica desde la rama main' }
    if (git status --porcelain) { throw 'Hay cambios sin confirmar (git status)' }
    git fetch -q origin main
    if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw 'main no coincide con origin/main (haz pull/push primero)' }
    if (git tag --list $tag) { throw "La etiqueta $tag ya existe: sube la version en src\Program.cs" }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'Falta GitHub CLI (gh)' }
}

# ---------------------------------------------------------------- compilacion
$out   = Join-Path $PSScriptRoot "dist\release\$version"
$stage = Join-Path $out '_build'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Step "Compilando Monitor de IPs $version"
$exe = Join-Path $stage 'MonitorIPs.exe'
& $csc /nologo /warn:4 /target:winexe /optimize+ /platform:anycpu "/out:$exe" '/win32icon:src\app.ico' `
    /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Security.dll `
    '/resource:src\web\index.html,index.html' '/resource:src\app.ico,app.ico' 'src\*.cs'
if ($LASTEXITCODE -ne 0) { throw 'Error de compilacion' }

$base = "MonitorIPs-$version"

Step 'Portable (.exe)'
Copy-Item $exe (Join-Path $out "$base-Portable.exe")

Step 'Portable (.zip)'
$zipDir = Join-Path $stage 'zip'
New-Item -ItemType Directory -Force $zipDir | Out-Null
Copy-Item $exe $zipDir
$leeme = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'installer\LEEME-portable.txt'), $utf8NoBom)
[IO.File]::WriteAllText((Join-Path $zipDir 'LEEME.txt'), ($leeme -replace "`r?`n", "`r`n"), $utf8Bom)
$lic = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LICENSE'), $utf8NoBom)
[IO.File]::WriteAllText((Join-Path $zipDir 'LICENCIA.txt'), ($lic -replace "`r?`n", "`r`n"), $utf8NoBom)
Compress-Archive -Path (Join-Path $zipDir '*') -DestinationPath (Join-Path $out "$base-Portable.zip")

Step 'Instalador (Inno Setup)'
& $iscc /Q "/DAppVersion=$version" "/DSourceExe=$exe" "/DOutputDir=$out" 'installer\MonitorIPs.iss'
if ($LASTEXITCODE -ne 0) { throw 'Error al compilar el instalador' }

Remove-Item $stage -Recurse -Force

Step 'SHA256SUMS.txt'
$sums = Get-ChildItem $out -File | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
}
[IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), [string[]]$sums, $utf8NoBom)

Get-ChildItem $out -File | Sort-Object Name | Format-Table Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } } -AutoSize

if (-not $Publish) {
    Write-Host "Archivos listos en $out" -ForegroundColor Green
    Write-Host 'Para publicar: .\release.ps1 -Publish'
    return
}

# ---------------------------------------------------------------- publicacion
Step 'Notas de la version'
$changelog = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CHANGELOG.md'), $utf8NoBom)
$section = [regex]::Match($changelog, "(?ms)^## \[?$([regex]::Escape($version))\]?[^\n]*\n(.*?)(?=^## |^\[)")
if (-not $section.Success) { throw "CHANGELOG.md no tiene una seccion para $version" }
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'installer\release-notes.md'), $utf8NoBom)
$notes = $template.Replace('{VERSION}', $version).Replace('{CHANGES}', $section.Groups[1].Value.Trim())
$notesFile = Join-Path $out 'notas.md'
[IO.File]::WriteAllText($notesFile, $notes, $utf8NoBom)

Step "Etiqueta $tag"
git tag -a $tag -m "Monitor de IPs $version"
git push -q origin $tag

Step 'Creando la Release en GitHub'
$assets = Get-ChildItem $out -File | Where-Object Name -ne 'notas.md' | Sort-Object Name | ForEach-Object FullName
gh release create $tag @assets --title "Monitor de IPs $version" --notes-file $notesFile --verify-tag --latest
if ($LASTEXITCODE -ne 0) { throw 'gh release create fallo' }
Remove-Item $notesFile
Write-Host "Publicada: $(gh release view $tag --json url --jq .url)" -ForegroundColor Green
