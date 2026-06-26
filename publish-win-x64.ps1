param(
    [string]$OutputPath = 'publish\win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$project = Join-Path $root 'QQDatabaseExplorer.Desktop\QQDatabaseExplorer.Desktop.csproj'
$output = if ([System.IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $root $OutputPath }
$output = [System.IO.Path]::GetFullPath($output)

$runningProcess = Get-CimInstance Win32_Process |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($output, [System.StringComparison]::OrdinalIgnoreCase) } |
    Select-Object -First 1
if ($runningProcess) {
    throw "Publish output is in use by process $($runningProcess.ProcessId): $($runningProcess.ExecutablePath)"
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

$publishArgs = @(
    'publish',
    $project,
    '--configuration',
    'Release',
    '--runtime',
    'win-x64',
    '--self-contained',
    'true',
    '--output',
    $output,
    '-p:PublishSingleFile=true',
    '-p:PublishTrimmed=false',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:IncludeAllContentForSelfExtract=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    '-p:UseSharedCompilation=false'
)

& dotnet @publishArgs

Get-ChildItem -LiteralPath $output -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "Published to $output"
