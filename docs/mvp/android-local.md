# Android local en disco D

Herramientas y cachés: `D:\Development\PedidosYa`. Las instalaciones existentes de C se conservaron.

- JDK completo 19.0.2: `java/jdk-19`, con `javac` y `jlink`.
- Flutter 3.22.2: `flutter-existing`, copia de la instalación existente.
- SDK Android 36 y Build Tools 35.0.0: `android-sdk`.
- Gradle 8.13: `gradle`; caché de paquetes Dart: `pub-cache`.
- ADB, emulador 32.1.11 e imagen Google APIs Android 13: copiados del SDK existente a D.
- Dispositivo virtual: `avd/PedidosYa-API33.avd`, con teclado físico habilitado, 3 GB de RAM, cuatro núcleos y Vulkan desactivado.
- Datos Android y temporales: `android-user`, `temp` y `downloads`.

Flutter quedó configurado para utilizar el JDK y SDK en D. Las variables de los scripts afectan a la terminal actual; no cambian el PATH global. Windows y Flutter pueden conservar metadatos de usuario en su ubicación habitual; las herramientas y cachés del proyecto están en D.

## Resultado comprobado

- Análisis Flutter sin observaciones.
- Cuatro pruebas aprobadas: acceso sin sesión y exclusión de ofertas completadas, vencidas o inválidas.
- Compilación aprobada con `:app:assembleDebug`: 158 tareas, ocho ejecutadas y 150 reutilizadas en la corrida final.
- APK instalado por ADB en el emulador; pantalla de ingreso Delivery abierta.
- SDK 36 y Gradle se descargaron desde sus fuentes oficiales y se verificaron mediante sus checksums.

APK de pruebas: `.artifacts/mobile/Delivery-emulator-debug.apk`, 106882838 bytes.
SHA256: `249b4921bd1a0ff5edc15972d52740d33520aded7d1d7b1c1fd38e83678aa0f7`.
ABI: x86 y x86_64 para emuladores. No es el APK para teléfonos ARM.
Resumen: `.artifacts/mobile/build.json`.
Log aprobado: `D:\Development\PedidosYa\temp\gradle-app-debug.log`.

## Repetir la compilación

Desde `D:\GitLab\PedidosYa`:

```powershell
. ./scripts/android-env.ps1
./scripts/build-android-local.ps1 -Connection Emulator -Verify
./scripts/start-android-emulator.ps1
adb -s emulator-5554 install -r ./.artifacts/mobile/Delivery-emulator-debug.apk
```

El script genera el APK actualizado en `clients/mobile/build/app/outputs/flutter-apk/app-debug.apk`; la copia de `.artifacts` conserva el binario validado de esta sesión. Para instalar una compilación nueva, usar el archivo recién generado.

El build local compila específicamente la aplicación y sus dependencias. El empaquetado independiente de AAR de los complementos y Android Lint completo son verificaciones separadas, pendientes para publicación. No se desactivan comprobaciones de compilación Java/Kotlin ni validaciones de firmas.

La configuración alinea AGP 8.13.2 en los módulos, SDK de compilación 36 y Java/Kotlin en JVM 17. El NDK declarado es 27.0.12077973; esta compilación debug sin C++ propio no necesitó instalarlo.

API desde emulador: `http://10.0.2.2:8080`; web: `http://10.0.2.2:4200`. HTTP está habilitado explícitamente para esta compilación local de debug. Para USB, usar `-Connection Usb`, autorizar el teléfono desde su pantalla y configurar los túneles indicados por el script. La compilación y prueba en teléfono físico siguen pendientes.

## Aceptación del repartidor

Pedido preparado: `e24d9414-268a-4c9b-b836-a1f45e06507d`, Fogón de Lince DEMO, S/ 64.

Pendiente: completar en la app el ingreso OTP, disponibilidad, oferta, retiro con PIN, ruta y entrega con PIN; verificar seguimiento y cierre financiero desde el cliente. No se debe considerar aprobada esta fase por el solo hecho de generar el APK.

Se usarán únicamente cuentas y ubicaciones ficticias, correo en Mailpit y pagos sandbox. La configuración de publicación, firma propia, pruebas en dispositivos físicos e iOS siguen pendientes.

Fuentes: [SDK Manager](https://developer.android.com/tools/sdkmanager), [Android en Flutter](https://docs.flutter.dev/platform-integration/android/setup), [resolución de dependencias Gradle](https://docs.gradle.org/current/userguide/resolution_rules.html).

## Avance de interfaz — 23 de noviembre de 2026

El detalle de entrega se muestra separado de ofertas e historial, con regreso explícito a la lista y desplazamiento inicial al comienzo. Las ofertas y las entregas muestran el identificador del pedido. Este identificador permite distinguirlas, pero todavía no sustituye el resumen comercial deseable en una oferta (restaurante, distancia y ganancia).

Se permite inyectar el cliente API de CourierHome para verificar la interfaz sin red. Cinco pruebas Flutter aprobadas: acceso sin sesión, tres reglas de ofertas y apertura de entrega, motivo obligatorio, transición de llegada contra API simulada y regreso a la lista. Análisis Flutter sin observaciones. Esto no acredita una entrega real ni una prueba de integración con el backend.

APK debug de emulador recompilado con éxito (158 tareas Gradle): `.artifacts/mobile/Delivery-emulator-debug-2026-11-23.apk`. SHA-256: `d66648802f3669f02f269d2a478abab1e8c863d5e6d9254b3c22a3fade1ce5de`. Log local: `D:/Development/PedidosYa/temp/mvp-courier-build.log`. El binario permanece fuera de Git.

La aceptación integral nativa de retiro, ruta, GPS, entrega y cierre financiero permanece pendiente. No se instaló ni se probó esta nueva compilación en el emulador durante este avance. Continúan pendientes la firma de distribución, el dispositivo físico e iOS.
