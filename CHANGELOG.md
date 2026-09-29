# Registro de cambios

## 2.0.0 — 2026-09-29

Primera versión estable de Open Control Edge, con el trabajo de las tareas 1–8:

- Renombrado EdgeWidget a Open Control Edge y aplicación WPF para Windows 11 / .NET 8.
- Anillos de uso y tarjetas para Claude, Codex, Cursor, OpenCode, DeepSeek y OpenRouter, con vistas Sesión y Total.
- Temperaturas y carga de CPU y GPU NVIDIA mediante LibreHardwareMonitor.
- **Requisito vital para leer temperatura CPU: instalar PawnIO**, controlador firmado que permite el acceso de LibreHardwareMonitor a los sensores. El instalador detecta si falta y ofrece instalarlo mediante winget; también puede descargarse desde [pawnio.eu](https://pawnio.eu). Sin PawnIO se muestra cómo instalarlo en los anillos de temperatura.
- Claves de DeepSeek y OpenRouter protegidas con DPAPI CurrentUser; lectura de credenciales locales en solo lectura.
- Modos Fijado y Automático, menú de bandeja, ajustes persistentes y renderizado de capturas `--snapshot`.
- Instalación desde PowerShell en Archivos de programa y arranque mediante tarea programada.
- Compilación Release autocontenida distribuida como ZIP de la carpeta de aplicación.
