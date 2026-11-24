# Estado de No molestar / Hora de silencio
$quiet = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.quietmomentsprofile' -ErrorAction SilentlyContinue
Write-Host '--- QuietMoments ---'
$quiet | Format-List *

# Fallback: perfil de configuraciones
$settings = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings' -ErrorAction SilentlyContinue
Write-Host '--- Settings globales ---'
$settings | Format-List ToastEnabled, NOC_GLOBAL_SETTING_TOASTS_ENABLED

# Lista de apps con notificaciones desactivadas
Write-Host '--- Apps con Enabled=0 ---'
Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings' |
    ForEach-Object {
        $p = Get-ItemProperty $_.PSPath
        if ($p.Enabled -eq 0) { $_.PSChildName }
    }
