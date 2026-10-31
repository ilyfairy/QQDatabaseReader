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

# 裁剪用 partial: 只裁标记为可裁剪的程序集 (运行时, 框架等), 自己的程序集不裁, EF Core 读库用到的反射不受影响.
# 不能用 Native AOT: EF Core 按每个库的实际表结构动态建模型和查询. 内置 COM 互操作没用到, 关掉才能裁剪
& dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=true `
    -p:TrimMode=partial `
    -p:BuiltInComInteropSupport=false `
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
