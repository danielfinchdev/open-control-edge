# Registro de cambios

## 2.1.0 — sin publicar

Comportamiento nativo: sin scripts de PowerShell ni tareas hechas con scripts.

- **Instalación nativa.** Al abrir el ejecutable fuera de `C:\Program Files\OpenControlEdge` aparece la ventana
  «Instalar Open Control Edge»: con el único UAC copia la aplicación (carpeta temporal, SHA-256 de cada archivo,
  cambio con vuelta atrás, comprobación de permisos), protege la carpeta de datos (sin enlaces, solo administradores,
  integridad alta), migra desde EdgeWidget (conserva su carpeta y los ajustes), registra el inicio con Windows en el
  Programador de tareas (al iniciar sesión, sin retraso, elevado, prioridad normal) y arranca la copia instalada.
  «Usar sin instalar» o `--portable` la abren sin instalar. Menú de bandeja: «Iniciar con Windows» y «Desinstalar…».
  Se eliminan `tools\instalar.ps1` y `tools\claude-sesion.ps1`.
- **Arranque con datos en 1–2 s.** La última lectura se guarda en `cache.json` (sin secretos) y se pinta al abrir.
  Publicación sin compresión y con ReadyToRun. Medido el 29-09-2026 en este portátil (i7-8750H, 8 GB, RAM al 87 %),
  sin elevar y con `cache.json`, tiempo desde el inicio del proceso hasta el primer fotograma con datos; «1.ª» es
  la primera ejecución tras publicar (archivos recién escritos, así que no es un arranque en frío de verdad):

  | Publicación | 1.ª ejecución | En caliente (media de 3) | Primera lectura de red | Memoria a los 15 s (WS / privada) |
  |---|---|---|---|---|
  | Un archivo comprimido (2.0) | 2,22 s* | 1,38 s | 1,92 s | 260 / 163 MB |
  | Sin comprimir | 2,41 s | 1,13 s | 1,59 s | 149 / 94 MB |
  | **Sin comprimir + ReadyToRun (elegida)** | **1,46 s** | **0,83 s** | **1,32 s** | **151 / 96 MB** |
  | Comprimido + ReadyToRun | 1,84 s | 1,46 s | 1,94 s | 260 / 207 MB |

  \* sin caché todavía. ZIP: 60 MB → 67 MB.
- **Clic en el anillo de Claude: renueva la sesión sin abrir ninguna ventana.** Ejecuta la CLI de Claude Code como
  usuario normal (token del Explorador, `CreateProcessWithTokenW`), oculta, con la petición mínima y 60 s de límite;
  la tarjeta muestra «Renovando sesión…» y luego el resultado. Renovación automática en segundo plano
  (`autoRenewClaude`) dentro de los últimos minutos del token, como mucho una vez cada 30 min.
- **Clic en el anillo de CPU:** abre Configuración › Sistema › Información, sin privilegios de administrador.
- **Anillo de RAM** con memoria física usada, en caché y confirmada. Clic: «Liberar RAM» (recorte de memoria de los
  procesos accesibles y purga de la lista standby), como mucho una vez por minuto; se desactiva con `ramCleanup`.
- **Gasto en la tarjeta**: Claude (créditos de uso, si pasan de cero), Codex (saldo de créditos si la cuenta los
  tiene) y Cursor (gasto bajo demanda en USD). Solo con cifras reales de la respuesta.
- **Firma de código en CI** con SignPath Foundation, condicional a que existan el secreto y las variables; política de
  firma en el README.
- Corregida la codificación de `EdgeWindow.xaml.cs` (el aviso de PawnIO dejaba de reconocerse).

## 2.0.0 — 2026-09-29

Primera versión de Open Control Edge (antes EdgeWidget 1.1).

- Renombrado EdgeWidget a Open Control Edge y aplicación WPF para Windows 11 / .NET 8.
- Anillos de uso y tarjetas para Claude, Codex, Cursor, OpenCode, DeepSeek y OpenRouter, con vistas Sesión y Total.
- Temperaturas y carga de CPU y GPU NVIDIA mediante LibreHardwareMonitor.
- **Requisito vital para leer temperatura CPU: instalar PawnIO**, controlador firmado que permite el acceso de LibreHardwareMonitor a los sensores. El instalador detecta si falta y ofrece instalarlo mediante winget; también puede descargarse desde [pawnio.eu](https://pawnio.eu). Sin PawnIO se muestra cómo instalarlo en los anillos de temperatura.
- Claves de DeepSeek y OpenRouter protegidas con DPAPI CurrentUser; lectura de credenciales locales en solo lectura.
- Modos Fijado y Automático, menú de bandeja, ajustes persistentes y renderizado de capturas `--snapshot`.
- Instalación desde PowerShell en Archivos de programa y arranque mediante tarea programada.
- Nueva interfaz: pestaña negra con curvas cóncavas, anillos con el logo de cada IA y color por proveedor (rojo por encima del 85 %), tarjeta tipo bocadillo y tipografía Geist.
- Los anillos de IA solo aparecen si esa IA está instalada en el PC (ajustable con `providers`).
- Detección del plan de cada cuenta (Free / Go / Plus / Pro / Max…).
- Eliminado el anillo manual de Grok Bot, sustituido por Cursor automático.
- Seguridad: DLL nativas junto al ejecutable (ya no se extraen a una carpeta del usuario), escrituras de datos endurecidas y auditoría completa.
- Compilación Release autocontenida distribuida como ZIP de la carpeta de aplicación.
