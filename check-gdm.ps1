Get-CimInstance Win32_Process -Filter "Name like 'GDM%'" |
    Select-Object ProcessId, Name, CreationDate, ExecutablePath, CommandLine |
    Format-List
