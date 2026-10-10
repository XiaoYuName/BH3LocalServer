param(
    [string]$Output = (Join-Path $PSScriptRoot 'dist\BH3Capture-1.0.0-win-x64-single'),
    [string]$Archive
)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
if (-not $Archive) { $Archive = $Output + '.zip' }
$Archive = [IO.Path]::GetFullPath($Archive)
if (Test-Path -LiteralPath $Output) { throw "Output already exists: $Output" }
if (Test-Path -LiteralPath $Archive) { throw "Archive already exists: $Archive" }
Push-Location $PSScriptRoot
try {
    & dotnet run --project Capture.Check/Capture.Check.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Isolated checks failed' }
    & dotnet publish Capture.App/Capture.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $Output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $files = @(Get-ChildItem -LiteralPath $Output -File -Recurse)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'BH3Capture.exe') { throw 'Publish did not produce exactly one executable' }
    New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Archive)) | Out-Null
    Compress-Archive -LiteralPath $files[0].FullName -DestinationPath $Archive -CompressionLevel Optimal
    ((Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($Archive)) |
        Set-Content -LiteralPath ($Archive + '.sha256') -Encoding ASCII
    Write-Output "Ready: $Output"
    Write-Output "Share: $Archive"
}
finally { Pop-Location }
