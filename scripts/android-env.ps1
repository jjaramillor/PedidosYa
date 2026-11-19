param([string]$ToolRoot='D:\Development\PedidosYa')
$ErrorActionPreference='Stop'
if([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($ToolRoot)) -ne 'D:\'){throw 'Las herramientas deben permanecer en el disco D.'}
$jdk=Get-ChildItem (Join-Path $ToolRoot 'java') -Directory | Where-Object {(Test-Path (Join-Path $_.FullName 'bin/javac.exe')) -and (Test-Path (Join-Path $_.FullName 'bin/jlink.exe'))} | Select-Object -First 1
if(-not $jdk){throw 'Falta un JDK completo con javac y jlink en D:\Development\PedidosYa\java.'}
$env:JAVA_HOME=$jdk.FullName
$env:ANDROID_HOME=Join-Path $ToolRoot 'android-sdk'
$env:ANDROID_SDK_ROOT=$env:ANDROID_HOME
$env:ANDROID_USER_HOME=Join-Path $ToolRoot 'android-user'
$env:ANDROID_AVD_HOME=Join-Path $ToolRoot 'avd'
$env:GRADLE_USER_HOME=Join-Path $ToolRoot 'gradle'
$env:PUB_CACHE=Join-Path $ToolRoot 'pub-cache'
$env:TEMP=Join-Path $ToolRoot 'temp'
$env:TMP=$env:TEMP
$env:FLUTTER_SUPPRESS_ANALYTICS='true'
$env:CI='true'
foreach($path in @($env:ANDROID_USER_HOME,$env:ANDROID_AVD_HOME,$env:GRADLE_USER_HOME,$env:PUB_CACHE,$env:TEMP)){New-Item -ItemType Directory -Force -Path $path|Out-Null}
$flutterRoot=if(Test-Path "$ToolRoot\flutter\bin\flutter.bat"){"$ToolRoot\flutter"}else{"$ToolRoot\flutter-existing"}
$env:PATH="$flutterRoot\bin;$env:JAVA_HOME\bin;$env:ANDROID_HOME\platform-tools;$env:ANDROID_HOME\emulator;$env:PATH"
Write-Host "Entorno Android en D activo para esta terminal: $ToolRoot"


