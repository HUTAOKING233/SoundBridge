param(
    [string]$PublishDir = "dist\publish",
    [string]$OutDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishPath = if ([IO.Path]::IsPathRooted($PublishDir)) { $PublishDir } else { Join-Path $root $PublishDir }
$publish = (Resolve-Path -LiteralPath $publishPath).Path
$out = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
$stage = Join-Path $out "SoundBridge"

foreach ($file in @("SoundBridge.exe", "Assets\App.ico")) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) {
        throw "发布目录缺少 $file。请先运行 dotnet publish。"
    }
}

$bundled = -not (Test-Path -LiteralPath (Join-Path $publish "SoundBridge.deps.json"))
$selfContained = $bundled -or (Test-Path -LiteralPath (Join-Path $publish "coreclr.dll"))
if ($bundled) {
    foreach ($file in @("DirectWriteForwarder.dll", "System.Printing.dll", "ijwhost.dll")) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) {
            throw "捆绑版缺少 $file。请使用当前项目重新发布。"
        }
    }
}

$kind = if ($selfContained) { "portable" } else { "framework-dependent" }
$zip = Join-Path $out "SoundBridge-win-x64-$kind.zip"
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $zip)) {
    throw "输出目录已包含发布产物，请另选 -OutDir。"
}
$files = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
    $_.Extension -ne ".pdb" -and $_.Name -notlike "*.runtimeconfig.dev.json" -and
    $_.Name -notin @("log.txt", "SoundBridge.log", "startup-crash.txt")
}

New-Item -ItemType Directory -Path $stage -Force | Out-Null
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
    $destination = Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $stage
Compress-Archive -LiteralPath $stage -DestinationPath $zip
Get-Item -LiteralPath $zip
