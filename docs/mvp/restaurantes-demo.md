# Restaurantes de demostración de Lima

Carga local realizada mediante `scripts/seed-restaurants-lima.ps1`: 8 restaurantes y 32 productos, con 2 restaurantes por distrito: Lince, La Victoria, Miraflores y San Isidro. Todos los nombres terminan en DEMO. Nombres, cartas, precios, documentos y datos comerciales son ficticios. Las ubicaciones son aproximadas y la cobertura rectangular no representa límites oficiales de distritos.

Los restaurantes están aprobados, con stock de prueba de 100 por producto, pedido mínimo S/ 15 y preparación de 20 minutos. El horario 24/7 es únicamente para facilitar pruebas. No se cargaron imágenes ni valoraciones inventadas.

El catálogo público se verificó por HTTP desde un punto de cada distrito: los ocho restaurantes aparecen abiertos dentro de un radio de 10 km. El resumen de identificadores se conserva sin tokens en `.artifacts/validation/restaurantes-lima-demo.json`.

El script requiere la API local en sandbox, Mailpit y las cuentas ficticias de la validación previa. Reutiliza comercios existentes por nombre para evitar duplicados al repetir una carga completa. No modifica cuentas personales ni las direcciones del usuario.

Para verlos, abrir Restaurantes y pulsar Buscar. Si la cuenta tiene una dirección fuera de Lima central, seleccionar una dirección dentro de la cobertura antes de buscar.
