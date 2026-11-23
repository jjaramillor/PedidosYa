# Revisión de requerimientos, alcance y MVP

Fecha de revisión: 19 de noviembre de 2026.

## Línea base

Fuentes: secciones 6–9 de `Alcance_funcional_plataforma_delivery.docx`, matriz preservada en `matriz-original.json` y documento de arquitectura. Los documentos originales se conservan intactos. Esta revisión no cambia el alcance aprobado ni equivale a una nueva certificación del código.

Objetivo: completar una compra de restaurante con entrega inmediata, desde la confirmación hasta la entrega y la liquidación disponible, con auditoría y operación por los usuarios sin intervención técnica.

La sección 7 incluye 44 funcionalidades con Entrega = MVP: 41 «Debe» y 3 «Debería». La instrucción de implementar toda la entrega MVP incluye ambas prioridades.

| Dominio | Funcionalidades MVP |
|---|---:|
| Identidad | 4 |
| Cliente | 8 |
| Comercio | 8 |
| Reparto | 9 |
| Pagos | 6 |
| Promociones | 1 |
| Administración y soporte | 8 |

Las tres «Debería» incluidas son CLI-010 (calificaciones), PAG-002 (efectivo configurable) y PRO-001 (cupones básicos).

## Límites del producto

Una ciudad, una moneda, restaurantes y entrega inmediata. La implementación local usa Lima, PEN y America/Lima; los distritos con datos DEMO no equivalen a cobertura comercial aprobada.

Canales: cliente web y móvil; comercio operativo y portal; repartidor móvil; administración y soporte web. Actualmente el comercio opera desde Angular: la equivalencia con el canal tablet/móvil descrito en el alcance debe aceptarse expresamente o completarse. El cliente y el repartidor comparten proyecto Flutter.

Fuera del MVP por matriz: favoritos, repetir pedido, reemplazos de productos, POS/carga masiva, pedidos agrupados, billetera, fraude avanzado, promociones avanzadas, suscripción, publicidad, marketplace multicategoría, picking, dark stores, delivery empresarial y crédito. Identidad federada está en Fase 2, aunque existe implementación adicional.

## Evidencia disponible y límites

| Área | Estado sustentado por documentación existente | Pendiente para aceptación |
|---|---|---|
| Backend | 74 pruebas aprobadas; recorrido HTTP sandbox con tarjeta y efectivo documentado | Vincular cada requisito y regla a criterios y evidencia; no declarar 44 funcionalidades aceptadas por disponer de endpoints |
| Cliente y comercio web | Compra, aceptación, preparación y pedido listo recorridos el 4 de noviembre | Seguimiento hasta entrega, valoración, sesiones simultáneas y excepciones por interfaz |
| Repartidor Android | APK debug compilado e instalado; análisis y cuatro pruebas Flutter aprobados | Concluir recorrido nativo de retiro, ruta, GPS, entrega y ganancias; consolidar evidencia |
| Finanzas | Cierre y liquidación ficticia por HTTP documentados | Aceptación por interfaz de conciliación, reembolsos, rendición y liquidaciones |
| Proveedores | Sandbox, Mailpit y mapas locales configurados | Verificar pagos y webhook HTTPS, correo y mapas en entorno de despliegue |
| Distribución móvil | APK local de emulador | Dispositivos físicos, permisos/ciclo de vida, firma propia; iOS si se incluye en el lanzamiento |

No se ejecutaron de nuevo estas pruebas durante esta revisión documental. El MVP está en implementación y aceptación parcial; no hay evidencia suficiente para declararlo terminado ni aprobado para producción.

## Diferencias que requieren resolución

1. **GPS y tiempo real (CLI-008, REP-007, RN-015):** Flutter comparte ubicación con la app abierta y el cliente móvil consulta cada diez segundos. Definir y probar el comportamiento al bloquear pantalla, perder conexión o pasar a segundo plano, junto con la retención de posiciones.
2. **Asignación (REP-004, RN-008):** el código revisado filtra aprobación, disponibilidad, zona, posición reciente y entrega activa, y ordena por cercanía. No se observa en esa selección un filtro de compatibilidad de vehículo ni una ponderación por preparación; la capacidad es una entrega. Resolver la diferencia con RN-008 mediante implementación o decisión de producto documentada.
3. **Notificaciones:** el entorno prueba correo local; debe precisarse si el lanzamiento exige push o SMS. OTP actual usa correo. La mención de varios canales en el alcance amplio no acredita una integración operativa de todos ellos.
4. **Liquidaciones (PAG-006):** registrar manualmente un pago no ejecuta una transferencia. Definir responsable, referencia y conciliación del desembolso antes de operar.
5. **Mapas, impuestos y comprobantes:** distancias/ETA son aproximaciones en línea recta, no rutas con tráfico; no hay geocodificación automática y el impuesto separado de la cotización es cero. Resolver tratamiento comercial por país antes de producción, como prevé la sección 9.
6. **Reglas amplias frente a matriz:** RN-007 contempla reemplazos, pero COM-008 los ubica en Fase 2. Mantener cancelación/incidencia esencial en MVP y registrar la excepción de alcance; no presentar reemplazos como ya incluidos o aceptados.
7. **Experiencia del repartidor:** durante la prueba iniciada se observaron ofertas sin contexto suficiente del restaurante/pedido y dificultad para acceder al detalle en una lista extensa. Corregir y verificar antes de exigir un recorrido sin ayuda técnica.

## Criterios de cierre

- Un cliente, comerciante y repartidor completan un pedido desde sus interfaces, sin SQL ni acciones técnicas para avanzar el negocio.
- Soporte resuelve cancelación, incidencia y reasignación autorizadas; los permisos impiden operaciones ajenas.
- Cobros, reembolsos, comisión, ganancia, efectivo y liquidación se reconcilian sin duplicados ante reintentos.
- Los cambios sensibles conservan actor, motivo y auditoría; ubicación y documentos respetan permisos y retención definida.
- Las 44 funcionalidades tienen criterio de aceptación, evidencia y resultado explícito; las diferencias quedan resueltas o aceptadas por producto.
- El entorno piloto dispone de proveedores verificados, observabilidad y procedimientos de respaldo y recuperación comprobados.

## Orden propuesto de trabajo

1. Resolver las diferencias de alcance y canal, preservando las 44 funcionalidades como línea base.
2. Terminar y documentar aceptación Android y cierre del pedido desde cliente/finanzas.
3. Ejecutar excepciones, roles y operación simultánea; completar la matriz de aceptación por funcionalidad y RN.
4. Preparar y validar entorno piloto con proveedores y dispositivos previstos.

Referencias: [trazabilidad](trazabilidad.md), [estado documentado](README.md), [aceptación web](aceptacion-web-cliente-comercio.md) y [Android local](android-local.md).
