param(
    [string]$OutputPath = 'publish\win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'QQDatabaseExplorer.Desktop\QQDatabaseExplorer.Desktop.csproj'
$output = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath
}
else {
    Join-Path $root $OutputPath
}
$output = [System.IO.Path]::GetFullPath($output)

& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:UseSharedCompilation=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$executable = Join-Path $output 'QQDatabaseExplorer.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published executable was not created: $executable"
}

Write-Host "Published to $output"
