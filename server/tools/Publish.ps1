param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repository = [IO.Path]::GetFullPath((Join-Path $root '..'))
$migrateLegacy = [string]::IsNullOrWhiteSpace($OutputDirectory)
$destination = if ($migrateLegacy) { Join-Path $root 'dist\win-x64' } else { [IO.Path]::GetFullPath($OutputDirectory) }
$legacyServer = Join-Path $root 'dist\server'
$ownedExecutables = @((Join-Path $destination 'Server\BH3.Server.exe'), (Join-Path $destination 'Launcher\BH3.Launcher.exe'))
if ($migrateLegacy) { $ownedExecutables += Join-Path $legacyServer 'BH3.Server.exe' }
foreach ($process in @(Get-Process -Name 'BH3.Server','BH3.Launcher' -ErrorAction SilentlyContinue)) {
    if ($ownedExecutables -contains $process.Path) { throw 'Close this package launcher and server before publishing.' }
}
$stage = Join-Path $root ('dist\stage-' + [guid]::NewGuid().ToString('N'))
Push-Location $root
try {
    & dotnet publish 'src\BH3.Server\BH3.Server.csproj' -c Release -r win-x64 --self-contained true --nologo -o (Join-Path $stage 'Server')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Push-Location (Join-Path $repository 'desktop')
    try {
        & dotnet publish 'src\BH3.Launcher\BH3.Launcher.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded --nologo -o (Join-Path $stage 'Launcher')
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    } finally { Pop-Location }
    $utf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $stage 'BH3.package.json'), '{"version":"1.3.0","launcher":"Launcher/BH3.Launcher.exe","server":"Server/BH3.Server.exe"}', $utf8)
    [IO.File]::WriteAllText((Join-Path $stage 'Start.bat'), "@echo off`r`nstart `"`" `"%~dp0Launcher\BH3.Launcher.exe`"`r`n", $utf8)
    Copy-Item -LiteralPath (Join-Path $root 'docs\PACKAGE_README.txt') -Destination (Join-Path $stage 'README.txt')
    # Archive the clean staging build before migrating any local settings or player data.
    $archive = if ($migrateLegacy) { Join-Path $root 'dist\BH3-Local-1.3.0-win-x64.zip' } else { $destination + '.zip' }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force

    # First migration only; original folders and existing destination files are retained.
    if ($migrateLegacy) {
        foreach ($directory in @('config','data')) {
            $old = Join-Path $legacyServer $directory
            $new = Join-Path $destination ('Server\' + $directory)
            if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $new)) {
                New-Item -ItemType Directory -Path (Join-Path $destination 'Server') -Force | Out-Null
                Copy-Item -LiteralPath $old -Destination (Join-Path $destination 'Server') -Recurse
                Write-Output ('Migrated legacy server ' + $directory)
            }
        }
        $oldSettings = Join-Path $repository 'desktop\dist\Data\settings.json'
        $newSettings = Join-Path $destination 'Launcher\Data\settings.json'
        if ((Test-Path -LiteralPath $oldSettings) -and -not (Test-Path -LiteralPath $newSettings)) {
            $settings = Get-Content -LiteralPath $oldSettings -Raw | ConvertFrom-Json
            if ($settings.ServerExe -eq (Join-Path $legacyServer 'BH3.Server.exe')) { $settings.ServerExe = '' }
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($newSettings)) -Force | Out-Null
            [IO.File]::WriteAllText($newSettings, ($settings | ConvertTo-Json -Depth 8), $utf8)
            Write-Output 'Migrated legacy launcher settings.'
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) {
        $relative = $file.FullName.Substring($stage.Length + 1)
        $target = Join-Path $destination $relative
        if ($relative.StartsWith('Server\config\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $target)) { continue }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
    foreach ($required in @('Launcher\BH3.Launcher.exe','Server\BH3.Server.exe','Server\config\server.json','Start.bat')) {
        if (-not (Test-Path -LiteralPath (Join-Path $destination $required))) { throw ('Incomplete package: ' + $required) }
    }
    Write-Output ('Published package: ' + $destination)
    Write-Output ('Start with: ' + (Join-Path $destination 'Launcher\BH3.Launcher.exe'))
    Write-Output ('Clean distribution archive: ' + $archive)
    Write-Output 'Existing configuration and player data preserved.'
} finally {
    Pop-Location
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $root 'dist')) + [IO.Path]::DirectorySeparatorChar
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if (-not $resolvedStage.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging cleanup path escaped dist.' }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
