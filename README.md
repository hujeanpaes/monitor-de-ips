# Monitor de IPs

Monitor para Windows que vigila varias IPs y servicios a la vez (**ping**, **puertos TCP** y **webs HTTP/HTTPS**), traza rutas con **tracert** y **descubre equipos activos** en un rango de red, todo desde una interfaz moderna.

Disponible como **instalador** o como **portable** (un único `.exe` de unos 175 KB, sin instalar nada). Usa .NET Framework 4 y Microsoft Edge, que ya vienen con Windows 10 y 11.

**Versión actual:** 1.8.0 · [Descargar](../../releases/latest) · [Historial de cambios](CHANGELOG.md)

---

## Funciones

| | |
|---|---|
| **Ping continuo** | Una tarjeta por IP con latencia actual, gráfica de los últimos 2 min, mín/prom/máx y % de pérdida. Verde = en línea, ámbar = latencia alta o pérdidas, rojo = caída. |
| **Servicios (TCP y web)** | Además del ping: comprueba un **puerto TCP** (escritorio remoto, SQL, impresora…) o una **web** (código HTTP y texto esperado, certificado HTTPS con aviso de caducidad). "Detectar puertos abiertos" encuentra los servicios de un equipo con un clic. La tarjeta muestra la causa del fallo: puerto cerrado, sin respuesta, HTTP 503… |
| **Vista de lista** | Alterna entre tarjetas y una **lista compacta** (una fila por comprobación, con minigráfica) para vigilar muchas IPs sin scroll. Se ordena por nombre, latencia, estado o pérdida. Tecla `V`. |
| **Modo pantalla (NOC)** | Para un monitor de pared: reloj, contadores grandes y disponibilidad de 24 h, con un mosaico que siempre cabe en pantalla y pone **las caídas primero** y en rojo. Mantiene la pantalla encendida y oculta el cursor. Tecla `M` (y `F11` para pantalla completa). |
| **Detalle** | Gráfica de 1, 5 o 15 min, jitter, enviados/recibidos, último error y consola con el formato de `ping -t`. |
| **Alertas** | Tras N fallos seguidos (por defecto 3) la IP pasa a *caída*: aviso en pantalla, pitido, contador en el título y notificación de Windows, también con la ventana cerrada. Al recuperarse avisa con la duración de la caída. |
| **Bandeja e inicio con Windows** | Sigue vigilando en la bandeja al cerrar la ventana y puede arrancar solo al encender el equipo. |
| **Tracert** | Traza salto a salto en vivo, con nombres DNS opcionales y botón para copiarla en formato `tracert`. |
| **Escanear red** | Rango `192.168.3.1 - 192.168.3.254`, `192.168.3.1-254` o `192.168.3.0/24` (hasta 4096 IPs). Muestra IP, nombre, MAC y latencia. En la red local detecta también, por ARP, equipos que bloquean el ping. Las detectadas se añaden al monitor con un clic. |
| **Organización** | Grupos, búsqueda por texto o por rango de IPs, filtros por estado (todas, en línea, caídas, pausadas) y pausa individual o global. |
| **Papelera** | Las IPs eliminadas, una a una o todas, se pueden recuperar durante 30 días. Las acciones de borrado piden confirmación y se pueden deshacer. |
| **Historial y disponibilidad** | Historial por minuto de cada IP (24 h, 7 días, 30 días): % de disponibilidad, caídas, tiempo caído y latencias, con gráfica y lista de caídas. Vista "Disponibilidad" de todas las IPs con exporte a CSV. |
| **Logs y exportes** | Log diario automático de eventos en CSV y exportación de eventos, resumen por IP y latencias (separador `;`, se abre directo en Excel). |
| **Interfaz** | Tema oscuro y claro, atajos de teclado (`N` añadir, `Ctrl+F` buscar, `V` tarjetas/lista, `M` modo pantalla) y diseño adaptable. |

## Requisitos

- Windows 10 u 11.
- .NET Framework 4.x (incluido en Windows).
- Microsoft Edge (incluido en Windows). Si no está, se usa Chrome, Brave o el navegador predeterminado.

## Descargas

Cada versión se publica en **[Releases](../../releases/latest)** con estas opciones:

| Archivo | Cuándo usarlo |
|---|---|
| `MonitorIPs-<versión>-Setup.exe` | **Instalador.** Menú Inicio, acceso directo, desinstalador y actualización encima de la versión anterior. No pide permisos de administrador (opcionalmente, para todos los usuarios). |
| `MonitorIPs-<versión>-Portable.exe` | **Portable.** Un solo archivo, sin instalar: ideal para USB o carpetas compartidas. |
| `MonitorIPs-<versión>-Portable.zip` | El portable comprimido, con `LEEME.txt`. |
| `SHA256SUMS.txt` | Huellas SHA-256 para verificar las descargas. |

> La primera vez, Windows puede mostrar **"Windows protegió su PC"** porque los ejecutables no tienen firma digital: *Más información → Ejecutar de todas formas*.

El monitor **avisa cuando hay una versión nueva** (franja bajo los filtros y notificación en la bandeja) y descarga el instalador o el portable según la copia que uses.

## Uso

1. **Instalador:** ejecuta el Setup y abre *Monitor de IPs* desde el menú Inicio o el escritorio.
   **Portable:** coloca el `.exe` en cualquier carpeta y ábrelo con doble clic.
2. Se abre una ventana propia (Edge en modo aplicación, sin pestañas).
3. **Al cerrar la ventana el monitor sigue vigilando en la bandeja del sistema** (junto al reloj):
   - El icono muestra un punto rojo si hay IPs caídas; al pasar el ratón se ve el resumen.
   - Clic en el icono: abre la ventana o la trae al frente.
   - Clic derecho: abrir, pausar o reanudar todas, sonido, iniciar con Windows y **Salir del monitor**.
   - Las caídas y recuperaciones se avisan con notificaciones de Windows aunque la ventana esté cerrada.
4. **Iniciar con Windows** (en *Ajustes*, en el menú de la bandeja o como opción del instalador): arranca directamente en la bandeja al encender el equipo.

Si prefieres que cerrar la ventana cierre el monitor, desactiva *Ajustes → Seguir en la bandeja al cerrar*.

### Dónde guarda los datos

| | Instalado | Portable |
|---|---|---|
| Ubicación | `%APPDATA%\MonitorIPs` | Junto al `.exe` (o `%APPDATA%\MonitorIPs` si la carpeta no permite escribir) |
| Al actualizar o desinstalar | Se conservan | Se conservan mientras no borres la carpeta |

| Archivo | Contenido |
|---|---|
| `config.json` | IPs, nombres, grupos, intervalos, papelera y ajustes (también la vista, el orden de la lista y el modo pantalla). Se puede editar a mano. |
| `history\AAAA-MM-DD.csv` | Historial por minuto e IP (disponibilidad y latencias). Se conserva 90 días por defecto (ajustable). |
| `logs\eventos-AAAA-MM-DD.csv` | Caídas, recuperaciones y cambios del día. |
| `exports\` | Archivos exportados desde el menú ⋮. |

Para llevar tus IPs a otro PC, copia `config.json` a la carpeta de datos del otro equipo. *Ajustes* muestra la carpeta en uso y si la copia es instalada o portable.

## Compilar

No hace falta instalar Visual Studio ni el SDK de .NET: se usa el compilador `csc.exe` que ya trae Windows.

```bat
build.bat
```

El resultado queda en `dist\MonitorIPs.exe`. Si falta `src\app.ico`, se genera con `tools\make-icon.ps1`.

## Publicar una versión

Requiere [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) y GitHub CLI con sesión iniciada.

1. Sube la versión en `src\Program.cs` (`const string Version` y atributos de ensamblado) y añade su sección en `CHANGELOG.md`.
2. Genera y revisa los archivos (quedan en `dist\release\<versión>\`):
   ```powershell
   .\release.ps1
   ```
3. Con los cambios ya en `main`, publica (crea la etiqueta `v<versión>` y la Release con instalador, portables y SHA256SUMS):
   ```powershell
   .\release.ps1 -Publish
   ```

## Arquitectura

```
MonitorIPs.exe (C#, .NET Framework 4)
 ├─ Servidor HTTP local (HttpListener, sólo localhost + token por sesión)
 │   ├─ /            → interfaz web embebida (src/web/index.html)
 │   └─ /api/*       → estado, IPs, tracert, escaneo, papelera, ajustes, exportes
 ├─ Un hilo de ping por IP (System.Net.NetworkInformation.Ping)
 └─ Abre Edge en modo --app apuntando al servidor local
```

| Archivo | Responsabilidad |
|---|---|
| `src/Program.cs` | Arranque, instancia única, servidor HTTP, rutas de la API, ventana y ciclo de vida. |
| `src/Tray.cs` | Icono de bandeja, menú, notificaciones con la ventana cerrada, enfoque de la ventana e inicio con Windows. |
| `src/History.cs` | Historial por minuto en disco, cálculo de disponibilidad, caídas y tramos para las gráficas. |
| `src/Checks.cs` | Comprobaciones: ping, puerto TCP y web (HTTP/HTTPS, certificados), y detección de puertos abiertos. |
| `src/Monitor.cs` | Motor de ping, buffer circular de muestras, estadísticas, detección de caídas, eventos y papelera. |
| `src/Tracer.cs` | Tracert mediante ping con TTL creciente. |
| `src/Scanner.cs` | Escáner de rangos IPv4 en paralelo (ping + ARP) y subredes locales. |
| `src/Config.cs` | Modelo de configuración, guardado atómico de `config.json`, logs CSV y utilidades. |
| `src/web/index.html` | Interfaz completa (HTML + CSS + JS, sin dependencias externas). |
| `build.bat` | Compilación con el `csc.exe` de Windows. |
| `release.ps1` | Genera instalador, portables y SHA256SUMS, y publica la Release. |
| `installer/MonitorIPs.iss` | Script del instalador (Inno Setup). |
| `installer/*` | Marcador de versión instalada, `LEEME` del portable y plantilla de notas de la Release. |
| `tools/make-icon.ps1` | Genera el icono de la aplicación. |

## Seguridad y privacidad

- El servidor escucha sólo en `localhost` en un puerto aleatorio, y cada llamada a la API exige un token generado en cada arranque.
- No se envía nada tuyo a internet: sólo los pings, conexiones y peticiones web hacia lo que configures.
- El **aviso de versión nueva** consulta una vez al día la última Release pública de este repositorio en `api.github.com` (sólo lectura, sin cuenta ni datos tuyos). Se puede desactivar en *Ajustes*. Nunca descarga ni ejecuta nada por sí mismo: "Descargar" abre el archivo en tu navegador.
- `dist/`, `config.json` y los logs están excluidos del repositorio (`.gitignore`).
