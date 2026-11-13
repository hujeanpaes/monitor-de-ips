# Historial de cambios

Formato basado en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/). Versionado [SemVer](https://semver.org/lang/es/).

## [1.9.0] - 2026-11-13

### Añadido
- **Informe PDF de disponibilidad**, desde el menú ⋮ o la ventana Disponibilidad.
  - **Periodo:** mes anterior, este mes, 24 h, 7 días, 30 días o fechas personalizadas.
  - **Alcance:** todas las comprobaciones o un grupo, con un título propio que se recuerda.
  - **Contenido:**
    - Resumen: disponibilidad global ponderada, caídas, tiempo caído, peor comprobación y latencia media.
    - Gráfica de disponibilidad por día (o por hora en 24 h) con el objetivo del 99,9 %.
    - Detalle por comprobación agrupado y ordenado de peor a mejor, con su cobertura de datos.
    - Registro de caídas, incluidas las que siguen en curso.
  - **Colores:** verde ≥ 99,9 %, ámbar ≥ 99 % y rojo < 99 %.
  - Se crea con el Edge del propio equipo (sin instalar nada), se guarda en `exports\` y se abre al terminar.
  - Si no se puede crear el PDF, se guarda como página web para imprimirla con Ctrl+P.

### Cambiado
- El historial admite periodos de calendario (por ejemplo, "octubre completo") además de las últimas 24 h, 7 días y 30 días.

## [1.8.0] - 2026-11-13

### Añadido
- **Vista de lista compacta**: alterna con las tarjetas desde la barra de filtros (o con la tecla `V`). Una fila por comprobación con estado, latencia, minigráfica de 2 min, promedio, máximo y pérdida. Se ordena por nombre, latencia, estado o pérdida pulsando las cabeceras. Se adapta al ancho disponible, también con el panel de eventos abierto.
- **Modo pantalla (NOC)** para monitores de pared: desde el botón de la cabecera, el menú ⋮ o la tecla `M`.
  - Muestra reloj, contadores grandes de caídas, en línea y pausadas, y la disponibilidad de las últimas 24 h.
  - Un mosaico que siempre cabe en pantalla, ordenado por gravedad: las caídas primero y en rojo, con su causa y el tiempo caído.
  - Con más de 60 comprobaciones, las que van bien pasan a pastillas pequeñas.
  - Respeta la búsqueda activa (por ejemplo, un solo grupo), mantiene la pantalla encendida y oculta el cursor.
  - Se sale con `Esc`; `F11` pone la ventana a pantalla completa.
- La vista elegida, el orden de la lista y el modo pantalla se recuerdan al reabrir (en `config.json`).

## [1.7.0] - 2026-11-13

### Añadido
- **Umbrales por comprobación**: en Añadir / Editar, sección "Alertas de esta comprobación" con *latencia alta* y *fallos seguidos para darla por caída* propios. Vacíos usan el valor general. El detalle indica "Umbral propio".
- **Aviso de versión nueva**: el monitor consulta la última Release pública en GitHub (al minuto de arrancar y una vez al día, o a mano con "Buscar actualizaciones"). Si hay una más nueva muestra una franja con *Novedades*, *Descargar* (instalador o portable según tu copia), *Omitir esta versión*, y avisa desde la bandeja si la ventana está cerrada. No descarga ni ejecuta nada por sí mismo.
- Ajuste "Buscar actualizaciones automáticamente" con el estado de la última comprobación.

### Cambiado
- La tarjeta, la línea de umbral de la gráfica y la consola usan el umbral efectivo de cada comprobación.
- El primer arranque crea dos ejemplos públicos (Google DNS y Cloudflare DNS) en lugar de IPs internas.
- El repositorio pasa a ser público para que las Releases se puedan descargar y consultar sin cuenta.

## [1.6.0] - 2026-11-13

### Añadido
- **Comprobación de servicios**, además del ping:
  - **Puerto TCP**: comprueba que un servicio acepta conexiones (escritorio remoto, base de datos, impresora…). Distingue *puerto cerrado* (el equipo responde pero el servicio no) de *sin respuesta*.
  - **Web (HTTP/HTTPS)**: comprueba el código de respuesta (por defecto 200-399, configurable: `200,301`, `2xx`…) y, opcionalmente, que la página contenga un texto. Admite certificados propios de equipos internos.
- **Detectar puertos abiertos**: prueba en paralelo los 20 puertos más comunes de un equipo y permite añadir uno o varios con un clic.
- Botón **"+ Servicio"** en el detalle de una IP para vigilar un puerto o una web del mismo equipo.
- **Certificados HTTPS**: el detalle muestra el titular y la fecha de caducidad; a 14 días o menos avisa en la tarjeta, en el panel de eventos, en el log y en la bandeja.
- "Varias a la vez" acepta `IP:puerto nombre` y `https://… nombre`.
- Tiempo de espera propio por comprobación (vacío = automático: ping el general, TCP 3 s, web 10 s).

### Cambiado
- Las tarjetas muestran el tipo (icono), el puerto o la URL y la **causa breve** del fallo: PUERTO CERRADO, SIN RESPUESTA, HTTP 503, TEXTO NO ENCONTRADO, DNS…
- El mismo equipo puede tener ping y varios servicios; los duplicados se detectan por comprobación.
- Exportes CSV con columna "Comprobación"; eventos y log indican el puerto o la URL.

### Corregido
- Desde la 1.4.0, las notificaciones de la bandeja y el aviso "sigue vigilando" mostraban caracteres raros en las tildes (`caÃ­da`, `âš `) por un error de codificación al publicar.

## [1.5.0] - 2026-11-13

### Añadido
- **Historial largo:** cada IP guarda por minuto sus pings, latencias y si estaba caída, en `history\AAAA-MM-DD.csv`. Antes sólo se conservaban los últimos 15 minutos en memoria.
- **Pestaña "Historial" en el detalle** con 24 h, 7 días y 30 días: disponibilidad %, número de caídas, tiempo caído, caída más larga, latencia media y máxima, gráfica con las caídas en rojo y lista de caídas (con "en curso").
- **Vista "Disponibilidad"** de todas las IPs: tabla ordenable con barra de disponibilidad, caídas, tiempo caído y latencia. Desde una fila se abre su historial, y el informe se puede **exportar a CSV**.
- Ajuste **"Guardar historial durante"** (90 días por defecto, de 7 a 365), que muestra cuánto ocupa. Lo antiguo se borra solo.

### Cambiado
- Las marcas del eje de latencia usan pasos redondos (10, 20, 25…) también en la gráfica en vivo.

## [1.4.1] - 2026-11-13

### Corregido
- El icono de la bandeja se veía pixelado y distinto al del programa: el `.ico` guarda sus imágenes en PNG, que .NET Framework reduce mal a 16-24 px. Ahora se dibuja con el mismo diseño del `.exe` y al tamaño real de la bandeja, nítido con cualquier escala de Windows (100 %, 125 %, 150 %…).
- El aviso de caídas en el icono pasa de un círculo que tapaba medio logo a una insignia roja pequeña en la esquina.

### Cambiado
- El menú de la bandeja también se ve nítido con escalado de pantalla (el proceso declara su DPI).

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

[1.9.0]: ../../releases/tag/v1.9.0
[1.8.0]: ../../releases/tag/v1.8.0
[1.7.0]: ../../releases/tag/v1.7.0
[1.6.0]: ../../releases/tag/v1.6.0
[1.5.0]: ../../releases/tag/v1.5.0
[1.4.1]: ../../releases/tag/v1.4.1
[1.4.0]: ../../releases/tag/v1.4.0
[1.3.1]: ../../releases/tag/v1.3.1
[1.3.0]: ../../releases/tag/v1.3.0
[1.2.0]: ../../releases/tag/v1.2.0
