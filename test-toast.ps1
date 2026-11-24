$payload = '["Prueba helper","linea de prueba"]'
Set-Content -Path 'C:\Users\User\AppData\Local\GDM\pending-toast-test123.json' -Value $payload -Encoding UTF8
Start-Process explorer.exe -ArgumentList '"C:\proyectos\input manager proyecto\bin\BuildCheck211\GDM.Notify.exe"'
