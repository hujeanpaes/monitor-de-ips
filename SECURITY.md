# Seguridad

## Cómo avisar de un problema

Si encuentras una vulnerabilidad, **no abras una incidencia pública**. Usa el aviso privado de GitHub: pestaña **Security → Report a vulnerability** de este repositorio. Indica la versión, los pasos para reproducirlo y su posible impacto. Respondo lo antes posible y publico la corrección en una versión nueva.

## Qué hace el programa con tus datos

- El servidor de la interfaz escucha **sólo en `localhost`**, en un puerto aleatorio, y cada llamada exige un token que cambia en cada arranque.
- Todos los datos (`config.json`, historial, logs y exportes) se guardan **en tu equipo**: junto al `.exe` en la versión portable o en `%APPDATA%\MonitorIPs` en la instalada.
- Conexiones a internet, aparte de las comprobaciones que tú configures:
  - **Aviso de versión nueva:** una consulta de sólo lectura al día a la API pública de Releases de GitHub. Se puede desactivar en *Ajustes*. Nunca descarga ni ejecuta nada por sí mismo.
  - **Avisos por Telegram** (sólo si los activas): se envían a `api.telegram.org` el nombre de la comprobación, su IP o URL, el grupo, la hora y el nombre del equipo, únicamente a los chats que elijas. El token del bot se guarda cifrado con DPAPI (ligado a tu usuario de Windows) y la interfaz nunca lo vuelve a mostrar.
- El informe PDF se genera en local con el navegador del equipo, sin servicios externos.

## Versiones con soporte

Sólo la última versión publicada en [Releases](../../releases/latest) recibe correcciones.
