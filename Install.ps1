[CmdletBinding(SupportsShouldProcess=$true)]
param([switch]$Startup)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$exe = Join-Path $projectRoot 'Dashboard\SVPServer.exe'
$config = Join-Path $projectRoot 'config.json'
if (!(Test-Path -LiteralPath $exe)) { throw 'Сначала соберите Dashboard\SVPServer.exe.' }
if (!(Test-Path -LiteralPath $config)) {
    if ($PSCmdlet.ShouldProcess($config, 'Создать шаблон конфигурации')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot 'config.example.json') -Destination $config
        Write-Host 'Настройте адреса и пути в config.json перед запуском.'
    }
}
$locations = @([Environment]::GetFolderPath('Programs'))
if ($Startup) { $locations += [Environment]::GetFolderPath('Startup') }
foreach ($dir in $locations) {
    $shortcutPath = Join-Path $dir 'SVPServer.lnk'
    if (Test-Path -LiteralPath $shortcutPath) { Write-Warning "Ярлык уже существует: $shortcutPath"; continue }
    if ($PSCmdlet.ShouldProcess($shortcutPath, 'Создать ярлык')) {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = Split-Path -Parent $exe
        $shortcut.Description = 'SVPServer'
        $shortcut.Save()
    }
}
