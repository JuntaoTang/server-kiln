param([string]$SourceDirectory = $PSScriptRoot, [switch]$NoDesktopShortcut)
$ErrorActionPreference = 'Stop'
$source = [System.IO.Path]::GetFullPath($SourceDirectory)
$program = Join-Path $env:LOCALAPPDATA 'Programs\ServerKiln'
if (-not (Test-Path -LiteralPath (Join-Path $source 'ServerKiln.exe'))) {
    throw 'Run install.ps1 from the extracted Windows package.'
}
if ($source.TrimEnd('\') -eq $program.TrimEnd('\')) {
    throw 'The application is already in its installation directory.'
}
$running = Get-Process -Name ServerKiln -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $program 'ServerKiln.exe') }
if ($running) { throw 'Quit ServerKiln from its tray menu before updating.' }
New-Item -ItemType Directory -Path $program -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $program -Recurse -Force
$shell = New-Object -ComObject WScript.Shell
$shortcutFolders = @([Environment]::GetFolderPath('Programs'))
if (-not $NoDesktopShortcut) { $shortcutFolders += [Environment]::GetFolderPath('Desktop') }
foreach ($folder in $shortcutFolders) {
    if (-not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Force -Path $folder | Out-Null }
    $shortcut = $shell.CreateShortcut((Join-Path $folder 'ServerKiln.lnk'))
    $shortcut.TargetPath = Join-Path $program 'ServerKiln.exe'
    $shortcut.WorkingDirectory = $program
    $shortcut.IconLocation = (Join-Path $program 'ServerKiln.exe') + ',0'
    $shortcut.Description = 'ServerKiln GPU server monitor'
    $shortcut.Save()
}
Write-Output "Installed ServerKiln for the current user: $program"
Write-Output 'Open the desktop or Start menu shortcut. Login startup is optional in Settings.'
