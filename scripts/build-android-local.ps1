param([ValidateSet('Emulator','Usb')][string]$Connection='Emulator',[switch]$Verify)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'android-env.ps1')
$mobileRoot=Join-Path (Split-Path -Parent $PSScriptRoot) 'clients/mobile'
$flutter=Join-Path $flutterRoot 'bin/flutter.bat'
if(-not(Test-Path $flutter)){throw 'Flutter no está disponible en D.'}
Push-Location $mobileRoot
try {
    & $flutter --no-version-check config --jdk-dir=$env:JAVA_HOME --android-sdk=$env:ANDROID_HOME
    if($LASTEXITCODE -ne 0){throw 'No se pudo configurar Flutter.'}
    & $flutter --no-version-check pub get
    if($LASTEXITCODE -ne 0){throw 'No se resolvieron las dependencias.'}
    if($Verify){
        & $flutter --no-version-check analyze
        if($LASTEXITCODE -ne 0){throw 'Falló el análisis Flutter.'}
        & $flutter --no-version-check test
        if($LASTEXITCODE -ne 0){throw 'Fallaron las pruebas Flutter.'}
    }
    $hostAddress=if($Connection -eq 'Emulator'){'10.0.2.2'}else{'127.0.0.1'}
    $targetPlatforms=if($Connection -eq 'Emulator'){'android-x64'}else{'android-arm,android-arm64,android-x64'}
    $versionLine=Get-Content 'pubspec.yaml' | Where-Object {$_ -match '^version:\s*([^\s#]+)'} | Select-Object -First 1
    $versionParts=($versionLine -replace '^version:\s*','') -split '\+',2
    $versionCode=if($versionParts.Count -gt 1){$versionParts[1]}else{'1'}
    @(
        "sdk.dir=$($env:ANDROID_HOME.Replace('\','/'))",
        "flutter.sdk=$($flutterRoot.Replace('\','/'))",
        'flutter.buildMode=debug',
        "flutter.versionName=$($versionParts[0])",
        "flutter.versionCode=$versionCode"
    ) | Set-Content 'android/local.properties' -Encoding utf8
    $defines=@("API_URL=http://${hostAddress}:8080","WEB_URL=http://${hostAddress}:4200",'ALLOW_HTTP=true') | ForEach-Object {[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($_))}
    # Build the application target; standalone plugin AAR publishing is separate.
    & "$mobileRoot/android/gradlew.bat" -p "$mobileRoot/android" :app:assembleDebug --console=plain "-Ptarget-platform=$targetPlatforms" '-Ptarget=lib/main.dart' '-Pbase-application-name=android.app.Application' "-Pdart-defines=$($defines -join ',')" '-Pdart-obfuscation=false' '-Ptrack-widget-creation=true' '-Ptree-shake-icons=false'
    if($LASTEXITCODE -ne 0){throw 'Falló la compilación del APK local.'}
    Write-Host "APK debug (arquitectura según Connection): $mobileRoot\build\app\outputs\flutter-apk\app-debug.apk"
    if($Connection -eq 'Usb'){Write-Host 'Con el teléfono autorizado: adb reverse tcp:8080 tcp:8080 y adb reverse tcp:4200 tcp:4200.'}
}
finally {Pop-Location}

