param(
    [string]$Makensis,
    [switch]$Portable,
    [string]$Payload = "dist\SoundBridge",
    [string]$OutName = "SoundBridge-Setup-1.0.0.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $root "installer\SoundBridge.nsi"
if (-not $Makensis) {
    $Makensis = (Get-Command makensis.exe -ErrorAction SilentlyContinue).Source
}
if (-not $Makensis) { throw "未找到 makensis.exe。请安装 NSIS 3.x 或传入 -Makensis。" }
if ([IO.Path]::GetFileName($OutName) -ne $OutName -or -not $OutName.EndsWith(".exe")) {
    throw "-OutName 必须是 exe 文件名。"
}

$payloadPath = if ([IO.Path]::IsPathRooted($Payload)) { $Payload } else { Join-Path $root $Payload }
$payloadDir = (Resolve-Path -LiteralPath $payloadPath).Path
if (-not (Test-Path -LiteralPath (Join-Path $payloadDir "SoundBridge.exe") -PathType Leaf)) {
    throw "缺少 SoundBridge.exe。请先用 tools/pack.ps1 准备发布目录。"
}

$files = Get-ChildItem -LiteralPath $payloadDir -Recurse -File | Where-Object {
    $_.Extension -ne ".pdb" -and $_.Name -notin @("log.txt", "SoundBridge.log", "startup-crash.txt")
}
$directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$commands = foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($payloadDir, $file.FullName)
    if ($relative -notmatch '^[\w.\\-]+$') { throw "无法写入卸载列表：$relative" }
    for ($dir = Split-Path -Parent $relative; $dir; $dir = Split-Path -Parent $dir) {
        [void]$directories.Add($dir)
    }
    'Delete "$INSTDIR\{0}"' -f $relative
}
$commands += $directories | Sort-Object Length -Descending | ForEach-Object {
    'RMDir "$INSTDIR\{0}"' -f $_
}

$tempDir = Join-Path $root ".build-tmp"
New-Item -ItemType Directory -Path $tempDir,(Join-Path $root "dist") -Force | Out-Null
$manifest = Join-Path $tempDir ("uninstall-{0}.nsh" -f [Guid]::NewGuid().ToString("N"))
try {
    Set-Content -LiteralPath $manifest -Value $commands -Encoding utf8BOM
    $options = @("/DOUTNAME=$OutName", "/DPAYLOAD=$payloadDir", "/DUNINSTALL_FILES=$manifest")
    if ($Portable) { $options += "/DPORTABLE" }
    & $Makensis @options $script
    if ($LASTEXITCODE -ne 0) { throw "makensis 失败，退出码 $LASTEXITCODE" }
}
finally {
    if (Test-Path -LiteralPath $manifest) { Remove-Item -LiteralPath $manifest }
}
Get-Item -LiteralPath (Join-Path $root "dist\$OutName")
