<#
.SYNOPSIS
    Devuelve el serial adb del Xiaomi: por USB si esta enchufado; si no, por Wi-Fi (depuracion inalambrica).
.DESCRIPTION
    La «Depuracion inalambrica» de Android 11+ escucha en un puerto aleatorio (30000-49999) que
    cambia al reiniciarla, y el PC ya esta emparejado. Se prueba el ultimo ip:puerto que funciono
    (guardado en %LOCALAPPDATA%\sOC\xiaomi-wifi.txt); si no responde, se busca el puerto abierto en
    la IP del movil (y, si esa tampoco, en toda la red 192.168.0.x). Escribe el serial en la salida
    (vacio si no hay movil) para usarlo con «adb -s».
.EXAMPLE
    $serial = & ..\Shared\xiaomi-conectar.ps1
    if ($serial) { adb -s $serial install -r app.apk }
#>
[CmdletBinding()]
param([string] $Ip = '192.168.0.15', [string] $UsbSerial = 'JJBILRLNOJL7JFY9')
$ErrorActionPreference = 'Continue'
$devices = adb devices | Select-Object -Skip 1 | Where-Object { $_ -match "`tdevice$" } | ForEach-Object { ($_ -split "`t")[0] }
if ($devices -contains $UsbSerial) { $UsbSerial; return }
# 127.0.0.1:NNNN es un emulador (MuMu), no el movil.
$wifi = $devices | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+:\d+$' -and $_ -notmatch '^127\.' } | Select-Object -First 1
if ($wifi) { $wifi; return }

$memo = Join-Path $env:LOCALAPPDATA 'sOC\xiaomi-wifi.txt'
function Probar($endpoint) {
    $out = adb connect $endpoint 2>&1
    if ("$out" -match '^connected|already connected') {
        Start-Sleep 1
        $ok = adb devices | Select-String ([regex]::Escape($endpoint) + "`tdevice")
        if ($ok) { return $true }
        adb disconnect $endpoint 2>$null | Out-Null
    }
    return $false
}
if (Test-Path $memo) {
    $last = (Get-Content $memo -Raw).Trim()
    if ($last -and (Probar $last)) { $last; return }
}
# Buscar el puerto abierto en la IP del movil (rapido: sockets en paralelo).
function BuscarPuerto($target) {
    $found = $null
    $tasks = @()
    foreach ($p in 30000..49999) {
        $c = New-Object Net.Sockets.TcpClient
        $tasks += [pscustomobject]@{ Port = $p; Client = $c; Task = $c.ConnectAsync($target, $p) }
        if ($tasks.Count -ge 800) {
            [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]($tasks | ForEach-Object Task), 600) | Out-Null
            foreach ($t in $tasks) { if ($t.Task.Status -eq 'RanToCompletion' -and $t.Client.Connected) { $found = $t.Port }; $t.Client.Dispose() }
            $tasks = @()
            if ($found) { return $found }
        }
    }
    if ($tasks.Count -gt 0) {
        [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]($tasks | ForEach-Object Task), 600) | Out-Null
        foreach ($t in $tasks) { if ($t.Task.Status -eq 'RanToCompletion' -and $t.Client.Connected) { $found = $t.Port }; $t.Client.Dispose() }
    }
    return $found
}
$port = BuscarPuerto $Ip
if ($port -and (Probar "${Ip}:$port")) {
    New-Item -ItemType Directory -Force (Split-Path $memo) | Out-Null
    Set-Content $memo "${Ip}:$port" -NoNewline
    "${Ip}:$port"; return
}
''
