param([string]$Name='PedidosYa-API33')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'android-env.ps1')
$avdPath=Join-Path $env:ANDROID_AVD_HOME ($Name+'.avd')
if(-not(Test-Path $avdPath)){throw "No existe el dispositivo virtual $Name en D."}
$devices = & "$env:ANDROID_HOME\platform-tools\adb.exe" devices
foreach ($device in $devices) {
    if ($device -match '^(emulator-\d+)\s+device$') {
        $serial = $matches[1]
        $runningName = & "$env:ANDROID_HOME\platform-tools\adb.exe" -s $serial emu avd name
        if ($runningName -contains $Name) { Write-Host "El emulador $Name ya está activo ($serial)."; return }
    }
}
# The emulator is an interactive test surface, so its window remains visible.
Start-Process -FilePath "$env:ANDROID_HOME\emulator\emulator.exe" -ArgumentList @('-avd',$Name,'-gpu','swiftshader_indirect','-no-audio','-no-snapshot','-no-boot-anim','-memory','3072','-cores','4','-feature','-Vulkan','-grpc-use-jwt') -WindowStyle Normal -RedirectStandardOutput (Join-Path $env:TEMP 'emulator.log') -RedirectStandardError (Join-Path $env:TEMP 'emulator-error.log')
Write-Host 'Al terminar de arrancar, instalar el APK debug. Para la prueba en Lince: adb -s emulator-5554 emu geo fix -77.036 -12.084'
