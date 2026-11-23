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

El 23 de noviembre se completaron en el emulador la llegada, retiro con PIN, inicio de ruta, GPS y entrega con PIN; el cliente web confirmó el pedido completado y registró su valoración. La sesión y la asignación se recuperaron de la prueba iniciada anteriormente. Los resultados y límites se detallan al final de esta guía.

Se usarán únicamente cuentas y ubicaciones ficticias, correo en Mailpit y pagos sandbox. La configuración de publicación, firma propia, pruebas en dispositivos físicos e iOS siguen pendientes.

Fuentes: [SDK Manager](https://developer.android.com/tools/sdkmanager), [Android en Flutter](https://docs.flutter.dev/platform-integration/android/setup), [resolución de dependencias Gradle](https://docs.gradle.org/current/userguide/resolution_rules.html).

## Avance de interfaz — 23 de noviembre de 2026

El detalle de entrega se muestra separado de ofertas e historial, con regreso explícito a la lista y desplazamiento inicial al comienzo. Las ofertas y las entregas muestran el identificador del pedido. Este identificador permite distinguirlas, pero todavía no sustituye el resumen comercial deseable en una oferta (restaurante, distancia y ganancia).

Se permite inyectar el cliente API de CourierHome para verificar la interfaz sin red. Cinco pruebas Flutter aprobadas: acceso sin sesión, tres reglas de ofertas y apertura de entrega, motivo obligatorio, transición de llegada contra API simulada y regreso a la lista. Análisis Flutter sin observaciones. Esto no acredita una entrega real ni una prueba de integración con el backend.

APK debug de emulador recompilado con éxito (158 tareas Gradle): `.artifacts/mobile/Delivery-emulator-debug-2026-11-23.apk`. SHA-256: `d66648802f3669f02f269d2a478abab1e8c863d5e6d9254b3c22a3fade1ce5de`. Log local: `D:/Development/PedidosYa/temp/mvp-courier-build.log`. El binario permanece fuera de Git.

La aceptación integral nativa de retiro, ruta, GPS, entrega y cierre financiero permanece pendiente. No se instaló ni se probó esta nueva compilación en el emulador durante este avance. Continúan pendientes la firma de distribución, el dispositivo físico e iOS.


## Aceptación Android y cierre del pedido 23 de noviembre de 2026

Resultado: pedido `e24d9414-268a-4c9b-b836-a1f45e06507d`, Fogón de Lince DEMO, entregado desde la aplicación Android y cerrado por el backend. Se instaló el APK actualizado del 23 de noviembre conservando la sesión y la asignación anteriores.

Recorrido ejecutado desde la interfaz nativa:

1. Recuperación de sesión y apertura del pedido asignado.
2. Llegada al restaurante con observación: `assigned` a `arrived`.
3. Retiro con PIN: `arrived` a `picked_up`.
4. Inicio de ruta: `picked_up` a `in_transit`.
5. Activación de ubicación con coordenadas sintéticas del emulador: cuatro posiciones persistidas. Al entregar desapareció el control de seguimiento y el número de posiciones permaneció en cuatro durante las comprobaciones posteriores.
6. Entrega con PIN del cliente: `in_transit` a `delivered`; el pedido pasó a `closed`.
7. Consulta de ganancias en la app: movimiento de entrega de S/4. El saldo global de la cuenta ficticia es S/-15,44 porque también contiene efectivo pendiente de rendición de pruebas anteriores; no representa una pérdida de esta entrega.

El cliente ingresó en la web mediante OTP de Mailpit, abrió el pedido marcado «Completado» y guardó una valoración de cinco estrellas para restaurante y repartidor con un comentario de prueba. El seguimiento web se comprobó después de entregar; esta ejecución no verifica animación del mapa ni propagación simultánea en vivo entre roles.

Comprobaciones financieras de solo lectura:

| Concepto | Importe PEN |
|---|---:|
| Total capturado en sandbox | 64,00 |
| Comercio | 49,30 |
| Repartidor | 4,00 |
| Plataforma | 10,70 |

Los comprobantes `capture` y `delivery` tienen suma cero; el reparto suma S/64. No se ejecutaron transferencias bancarias ni se marcaron liquidaciones pagadas durante este recorrido. Los PIN se consultaron mediante las API autorizadas del comercio y del cliente; no se editaron tablas para avanzar los estados.

Evidencia: [ruta y GPS activo](android-en-ruta-2026-11-23.jpg), [entrega completada](android-entregado-2026-11-23.jpg), [ganancias](android-ganancias-2026-11-23.jpg).

Entorno: emulador Android 13 en D, API Validation, PostgreSQL/PostGIS, Redis, Mailpit y pagos sandbox. La aprobación se limita al camino de entrega con PIN de este pedido. No cubre una nueva oferta, rechazo/vencimiento, pérdida de red, fotografía, segundo plano, dispositivo físico, iOS ni proveedores reales. No sustituye la aceptación completa de las 44 funcionalidades.

## Excepciones y recuperación de conexión

Validación automatizada del 23 de noviembre de 2026:

- Las ofertas vencidas se rechazan en el servidor tanto al aceptar como al rechazar, sin asignar un repartidor. La interfaz elimina las ofertas vencidas sin recarga manual.
- Un PIN de retiro incorrecto devuelve HTTP 422 y `invalid_delivery_pin`, conserva el estado `arrived` y consume un intento. Antes devolvía 401, que el cliente interpretaba como sesión caducada y podía reenviar tras renovar credenciales. El PIN correcto posterior permite continuar el recorrido.
- La recuperación de sesión conserva la credencial ante desconexión o respuestas 429, 500 y 503. Solo una respuesta 401 definitiva elimina la credencial. La pantalla permite reintentar la sesión guardada sin solicitar otro OTP cuando vuelve la conexión.
- Una acción cuya respuesta se pierde no se reenvía automáticamente. El botón «Actualizar estado de entrega» consulta el estado real para continuar; una prueba de interfaz simula que la llegada se confirmó en el servidor pero se perdió su respuesta.

Resultados: análisis Flutter sin observaciones y 15 pruebas aprobadas; cuatro pruebas de integración MVP aprobadas contra PostGIS/Redis de Testcontainers. Estas últimas incluyen recorridos tarjeta/efectivo con oferta vencida y PIN incorrecto antes del cierre balanceado. Evidencia local: `TestResults/mvp-excepciones.trx`.

Los fallos de red se simularon en el cliente HTTP y las pruebas de interfaz. Esta validación no equivale a desconectar físicamente un dispositivo ni a comprobar una pérdida de respuesta durante la rotación de un refresh token en el servidor. Continúan pendientes ese escenario, el bloqueo tras cinco PIN erróneos, las incidencias/devoluciones y la concurrencia entre roles.
