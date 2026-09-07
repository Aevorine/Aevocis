[CmdletBinding()]
param(
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$vswhere = Get-Command vswhere.exe -ErrorAction SilentlyContinue
if (-not $vswhere) {
    $vswherePath = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($vswherePath) { $vswhere = Get-Item -LiteralPath $vswherePath }
}
$vsInstallPath = if ($vswhere) {
    $vswhereExecutable = if ($vswhere.PSObject.Properties['Source'] -and $vswhere.Source) { $vswhere.Source } else { $vswhere.FullName }
    (& $vswhereExecutable -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
} else {
    ''
}
$vsDevCmd = if ($vsInstallPath) { Join-Path $vsInstallPath 'Common7\Tools\VsDevCmd.bat' } else { '' }
$isccCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$iscc = if ($isccCommand) {
    $isccCommand.Source
} else {
    $inno = Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue } |
        Where-Object { $_.DisplayName -like '*Inno Setup*' } |
        Select-Object -First 1
    if ($inno.InstallLocation) { Join-Path $inno.InstallLocation 'ISCC.exe' } else { '' }
}
$buildDir = Join-Path $projectRoot 'build\x64-release'
$portableDir = Join-Path $projectRoot 'dist\Aevocis-portable'
$manifest = Join-Path $projectRoot 'dist\SHA256SUMS.txt'

if (-not $vsDevCmd -or -not (Test-Path -LiteralPath $vsDevCmd)) { throw 'VS 2022 C++ toolchain not found.' }
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'Models\sensevoice\model.int8.onnx'))) {
    throw 'Models\sensevoice\model.int8.onnx is missing; release is blocked until the model resource is present.'
}

Push-Location $projectRoot
try {
    & cmd.exe /d /s /c "`"$vsDevCmd`" -arch=x64 && cmake --preset x64-release && cmake --build --preset x64-release && ctest --preset x64-release --output-on-failure"
    if ($LASTEXITCODE -ne 0) { throw "C++ release build failed with exit code $LASTEXITCODE" }

    New-Item -ItemType Directory -Force -Path $portableDir | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildDir 'aevocis.exe') -Destination $portableDir -Force
    Copy-Item -LiteralPath (Join-Path $buildDir 'aevocis_cli.exe') -Destination $portableDir -Force
    Copy-Item -LiteralPath (Join-Path $buildDir 'Models') -Destination $portableDir -Recurse -Force

    $files = @(Get-ChildItem -LiteralPath $portableDir -Recurse -File | Sort-Object FullName)
    $lines = foreach ($file in $files) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        '{0}  {1}' -f $hash, $file.FullName.Substring($portableDir.Length + 1)
    }
    Set-Content -LiteralPath $manifest -Value $lines -Encoding utf8NoBOM

    if (-not $SkipInstaller) {
        if (-not $iscc -or -not (Test-Path -LiteralPath $iscc)) { throw 'Inno Setup compiler not found.' }
        & $iscc (Join-Path $projectRoot 'installer\Aevocis.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
    }
    Write-Host "portable=$portableDir"
    Write-Host "manifest=$manifest"
}
finally {
    Pop-Location
}
