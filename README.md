# Monitor de IPs  ·  v1.2.0

Monitor portable de varias IPs por **ping continuo** y **tracert**, con interfaz moderna.
Un único `MonitorIPs.exe` (~150 KB), sin instalar nada: usa .NET Framework 4 y Microsoft Edge, que ya vienen con Windows 10/11.

## Uso
- Doble clic en `dist\MonitorIPs.exe`. Se abre una ventana propia (Edge en modo app, sin pestañas).
- **Añadir IP** (o tecla `N`): una IP, o *Varias a la vez* pegando una por línea (`IP nombre`, `nombre;IP`, o incluso líneas de `ping`).
- Clic en una tarjeta → detalle con gráfica (1/5/15 min), estadísticas y consola estilo `ping -t`.
- **Escanear red**: indica un rango (`192.168.3.1 - 192.168.3.254`, `192.168.3.1-254` o `192.168.3.0/24`, máx. 4096 IPs) o pulsa una de las redes de tu equipo. Muestra las IPs activas con nombre, MAC y latencia (en tu red local también detecta por ARP equipos que no responden a ping). Con **"Añadir N IPs al monitor"** se añaden todas las seleccionadas de un clic; las que ya estaban se omiten.
- El buscador también filtra por rango: escribe `192.168.3.1-50` o `10.64.12.0/24`.
- Icono de ruta en la tarjeta o botón **Tracert** → traza salto a salto en vivo (botón *Copiar* en formato tracert).
- Alertas: tras N fallos seguidos (por defecto 3) la IP pasa a **caída**: tarjeta roja, aviso, pitido y notificación de Windows. Al volver, aviso de **recuperada** con la duración.
- Menú ⋮: exportar eventos / resumen / latencias a CSV (`exports\`), abrir logs, pausar/reanudar todas, ajustes, salir.
- Al cerrar la ventana el monitor se detiene solo. Al volver a abrirlo, las IPs siguen ahí.

## Archivos (junto al .exe, portable)
- `config.json` – IPs, nombres, grupos, intervalos y ajustes (editable a mano).
- `logs\eventos-AAAA-MM-DD.csv` – historial diario de caídas y recuperaciones (separador `;`, abre directo en Excel).
- `exports\` – archivos exportados.

Si la carpeta no permite escritura, se usa `%APPDATA%\MonitorIPs`.

## Compilar
Ejecutar `build.bat` (usa el `csc.exe` incluido en Windows). Código en `src\`, interfaz en `src\web\index.html`.
