# Monitor de IPs

Monitor **portable** para Windows que vigila varias IPs a la vez por **ping continuo**, traza rutas con **tracert** y **descubre equipos activos** en un rango de red, todo desde una interfaz moderna.

Es un único `MonitorIPs.exe` de unos 150 KB. No hace falta instalar nada: usa .NET Framework 4 y Microsoft Edge, que ya vienen con Windows 10 y 11.

**Versión actual:** 1.2.0 · [Historial de cambios](CHANGELOG.md)

---

## Funciones

| | |
|---|---|
| **Ping continuo** | Una tarjeta por IP con latencia actual, gráfica de los últimos 2 min, mín/prom/máx y % de pérdida. Verde = en línea, ámbar = latencia alta o pérdidas, rojo = caída. |
| **Detalle** | Gráfica de 1, 5 o 15 min, jitter, enviados/recibidos, último error y consola con el formato de `ping -t`. |
| **Alertas** | Tras N fallos seguidos (por defecto 3) la IP pasa a *caída*: aviso en pantalla, pitido, contador en el título y notificación de Windows. Al recuperarse avisa con la duración de la caída. |
| **Tracert** | Traza salto a salto en vivo, con nombres DNS opcionales y botón para copiarla en formato `tracert`. |
| **Escanear red** | Rango `192.168.3.1 - 192.168.3.254`, `192.168.3.1-254` o `192.168.3.0/24` (hasta 4096 IPs). Muestra IP, nombre, MAC y latencia. En la red local detecta también, por ARP, equipos que bloquean el ping. Las detectadas se añaden al monitor con un clic. |
| **Organización** | Grupos, búsqueda por texto o por rango de IPs, filtros por estado (todas, en línea, caídas, pausadas) y pausa individual o global. |
| **Papelera** | Las IPs eliminadas, una a una o todas, se pueden recuperar durante 30 días. Las acciones de borrado piden confirmación y se pueden deshacer. |
| **Historial** | Log diario automático de eventos en CSV y exportación de eventos, resumen por IP y latencias (separador `;`, se abre directo en Excel). |
| **Interfaz** | Tema oscuro y claro, atajos de teclado (`N` añadir, `Ctrl+F` buscar) y diseño adaptable. |

## Requisitos

- Windows 10 u 11.
- .NET Framework 4.x (incluido en Windows).
- Microsoft Edge (incluido en Windows). Si no está, se usa Chrome, Brave o el navegador predeterminado.

## Uso

1. Descarga `MonitorIPs.exe` desde la sección [Releases](../../releases) o compílalo (ver abajo).
2. Colócalo en cualquier carpeta (escritorio, USB…) y ábrelo con doble clic.
3. Se abre una ventana propia (Edge en modo aplicación, sin pestañas). Al cerrarla, el monitor se detiene.

> La primera vez, Windows puede mostrar **"Windows protegió su PC"** porque el ejecutable no está firmado: *Más información → Ejecutar de todas formas*.

### Datos que genera (junto al .exe)

| Ruta | Contenido |
|---|---|
| `config.json` | IPs, nombres, grupos, intervalos, papelera y ajustes. Se puede editar a mano. |
| `logs\eventos-AAAA-MM-DD.csv` | Caídas, recuperaciones y cambios del día. |
| `exports\` | Archivos exportados desde el menú ⋮. |

Si la carpeta no permite escritura, se usa `%APPDATA%\MonitorIPs`. Para llevar tus IPs a otro PC, copia `config.json` junto al `.exe`.

## Compilar

No hace falta instalar Visual Studio ni el SDK de .NET: se usa el compilador `csc.exe` que ya trae Windows.

```bat
build.bat
```

El resultado queda en `dist\MonitorIPs.exe`. Si falta `src\app.ico`, se genera con `tools\make-icon.ps1`.

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
| `src/Program.cs` | Arranque, instancia única, servidor HTTP, rutas de la API, ventana y cierre automático. |
| `src/Monitor.cs` | Motor de ping, buffer circular de muestras, estadísticas, detección de caídas, eventos y papelera. |
| `src/Tracer.cs` | Tracert mediante ping con TTL creciente. |
| `src/Scanner.cs` | Escáner de rangos IPv4 en paralelo (ping + ARP) y subredes locales. |
| `src/Config.cs` | Modelo de configuración, guardado atómico de `config.json`, logs CSV y utilidades. |
| `src/web/index.html` | Interfaz completa (HTML + CSS + JS, sin dependencias externas). |
| `build.bat` | Compilación con el `csc.exe` de Windows. |
| `tools/make-icon.ps1` | Genera el icono de la aplicación. |

## Seguridad y privacidad

- El servidor escucha sólo en `localhost` en un puerto aleatorio, y cada llamada a la API exige un token generado en cada arranque.
- No se envía nada a internet: sólo los pings, trazas y consultas DNS/ARP hacia las IPs que configures.
- `dist/`, `config.json` y los logs están excluidos del repositorio (`.gitignore`).
