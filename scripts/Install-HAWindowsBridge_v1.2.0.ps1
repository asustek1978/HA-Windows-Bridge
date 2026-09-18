param([switch]$StartAfterInstall)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'HAWindowsBridge.exe'
$manifest = Join-Path $PSScriptRoot 'SHA256.txt'
if (-not (Test-Path -LiteralPath $source)) {
    throw 'Рядом с установщиком нет HAWindowsBridge.exe. Распакуйте весь архив сборки.'
}
if (-not (Test-Path -LiteralPath $manifest)) {
    throw 'Нет файла SHA256.txt. Распакуйте весь архив сборки.'
}
$expected = (Get-Content -LiteralPath $manifest -TotalCount 1).Trim().Split(' ')[0]
$actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or $actual -ne $expected) {
    throw 'Контрольная сумма EXE не совпала. Загрузите архив сборки заново.'
}
if (Get-Process -Name 'HAWindowsBridge' -ErrorAction SilentlyContinue) {
    throw 'Закройте клиент через значок в трее («Выход»), затем повторите установку.'
}

$folder = Join-Path $env:LOCALAPPDATA 'Programs\HAWindowsBridge'
New-Item -Path $folder -ItemType Directory -Force | Out-Null
$destination = Join-Path $folder 'HAWindowsBridge.exe'
Copy-Item -LiteralPath $source -Destination $destination -Force

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut(
    (Join-Path $startMenu 'HA Windows Bridge.lnk'))
$shortcut.TargetPath = $destination
$shortcut.WorkingDirectory = $folder
$shortcut.IconLocation = $destination
$shortcut.Save()

# Обновляем путь только тогда, когда автозапуск уже был разрешён пользователем.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$autostart = (Get-ItemProperty -Path $runKey -Name HAWindowsBridge -ErrorAction SilentlyContinue).HAWindowsBridge
if ($autostart) {
    Set-ItemProperty -Path $runKey -Name HAWindowsBridge -Value ('"' + $destination + '"')
}

Write-Host "Установлено: $destination"
Write-Host 'Настройки и ключи остаются в %LOCALAPPDATA%\HAWindowsBridge.'
if ($StartAfterInstall) { Start-Process -FilePath $destination }
