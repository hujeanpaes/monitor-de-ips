# Historial de cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/). Versionado [SemVer](https://semver.org/lang/es/).

## [1.4.0] - 2026-11-13

### Añadido
- **Bandeja del sistema:** al cerrar la ventana el monitor sigue vigilando junto al reloj. El icono muestra un punto rojo si hay caídas y un resumen al pasar el ratón; con clic abre o trae al frente la ventana, y con clic derecho tiene un menú para abrir, pausar o reanudar todas, sonido, inicio con Windows y salir.
- **Notificaciones de Windows con la ventana cerrada** cuando una IP cae o se recupera (con sonido si está activado).
- **Iniciar con Windows:** desde Ajustes, el menú de la bandeja o el instalador; arranca directamente en la bandeja (`--tray`). Si el portable cambia de carpeta, la ruta se corrige sola.
- Ajuste **"Seguir en la bandeja al cerrar"** (activado por defecto); desactivado, cerrar la ventana cierra el monitor como antes.

### Cambiado
- Abrir el `.exe` con el monitor ya en marcha trae su ventana al frente en lugar de abrir otra.
- "Salir del monitor" detiene el monitoreo y quita el icono de la bandeja.
- El desinstalador elimina también el inicio con Windows.

## [1.3.1] - 2026-11-13

### Corregido
- En ventanas sin maximizar la cabecera se superponía (título, fecha y filtros encima unos de otros). Ahora se adapta a cualquier tamaño, de 420 px en adelante, sin superposiciones ni desplazamiento horizontal.

### Cambiado
- Cabecera en dos franjas: arriba identidad y acciones; debajo, filtros de estado como control segmentado y búsqueda de ancho flexible.
- Al estrechar la ventana se simplifica por pasos: se oculta la fecha, los botones quedan sólo con icono, y en ventanas muy estrechas Escanear, Tracert, Sonido y Tema pasan al menú ⋮.
- Fecha y hora más cortas, en una sola línea.
- Detalle, escaneo y tracert se reorganizan en ventanas estrechas (estadísticas en 4 o 2 columnas, botones en una segunda línea).

## [1.3.0] - 2026-11-13

### Añadido
- **Instalador** (`MonitorIPs-<versión>-Setup.exe`): asistente en español, sin permisos de administrador (opción de instalar para todos los usuarios), accesos en el menú Inicio y el escritorio, desinstalador y actualización encima de versiones anteriores. Pide cerrar el monitor si está abierto.
- Descargas **portables** en cada versión: `.exe` suelto y `.zip` con instrucciones, más `SHA256SUMS.txt`.
- Script `release.ps1` que genera todos los archivos de una versión y la publica en GitHub Releases.
- Ajustes muestra si la copia es instalada o portable.

### Cambiado
- La versión instalada guarda sus datos (`config.json`, logs y exportes) en `AppData\Roaming\MonitorIPs`, así se conservan al actualizar o desinstalar. La portable sigue guardándolos junto al `.exe`.

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

[1.4.0]: ../../releases/tag/v1.4.0
[1.3.1]: ../../releases/tag/v1.3.1
[1.3.0]: ../../releases/tag/v1.3.0
[1.2.0]: ../../releases/tag/v1.2.0
