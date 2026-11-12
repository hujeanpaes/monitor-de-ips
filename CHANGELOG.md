# Historial de cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/). Versionado [SemVer](https://semver.org/lang/es/).

## [1.2.0] - 2026-11-12

### Añadido
- **Escanear red**: descubre IPs activas en un rango (`a.b.c.d - e.f.g.h`, `a.b.c.d-N`, CIDR) con ping y ARP, mostrando nombre, MAC y latencia, y las añade al monitor con un clic.
- Filtro por rango de IPs en el buscador.
- **Papelera** de 30 días: eliminar todas las IPs con confirmación, restaurar, borrar definitivamente y deshacer.
- Filtro "todas" junto a en línea / caídas / pausadas.
- Motivo real del último fallo en la tarjeta (tooltip) y en el detalle.

### Cambiado
- El escaneo en la red local consulta primero por ARP y evita los pings a IPs inexistentes (≈40 % más rápido en un /24).
- La interfaz refleja las acciones al instante, sin esperar al siguiente ciclo de actualización.
- Las altas en bloque guardan la configuración una sola vez.
- Las estadísticas por IP se calculan en una sola pasada.

### Corregido
- El guardado de `config.json` podía perderse si coincidía con otra modificación. Ahora es seguro entre hilos y atómico.
- Muestras duplicadas ocasionales en las gráficas.
- "Salir del monitor" no cerraba la aplicación si había otra ventana abierta.
- Al pegar líneas de `ping` en "Varias a la vez", el nombre se guardaba como "Respuesta desde…".
- El jitter de una IP sin respuestas mostraba "<1 ms" en lugar de "—".

### Eliminado
- Rutas internas de la API sin uso (`/api/heartbeat`, `/api/targets/reorder`).

## 1.0.0 - 2026-11-12

Versión previa al repositorio (no publicada).

### Añadido
- Monitor portable con ping continuo multi-IP, gráficas, estadísticas, alertas de caída y recuperación, tracert, grupos, ajustes, tema claro/oscuro, logs diarios y exportación a CSV.

[1.2.0]: ../../releases/tag/v1.2.0
