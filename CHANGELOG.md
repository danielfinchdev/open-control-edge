# Registro de cambios

## 2.2.0 — 2026-10-08

- **Vistas del panel.** El panel enseña una sola vista: **IA** (los anillos de las IA), **PC** (FPS, CPU, GPU, Modo juego
  y RAM) o **Personalizada** (la mezcla que quieras). Un cuarto botón redondo cambia de vista (IA → PC → Personalizada)
  con un fundido, y los botones pasan a ir de dos en dos: fijar/ocultar y vista arriba, ajustes y cerrar abajo. En
  Ajustes → Personalización eliges la vista activa y, por vista, qué anillos se ven y su orden con ↑↓ (por defecto
  alfabético; en PC, FPS primero). La escala del panel se calcula para la vista más grande (con lo que haya disponible)
  para que no salte al cambiar de vista.
- **Color de fondo del panel** (Ajustes → Personalización → Fondo del widget): colores predefinidos, `#RRGGBB` o
  deslizadores R/G/B; negro por defecto. Sobre fondos claros, el texto y los anillos pasan solos a la variante oscura.
- **Anillo de FPS** (el primero de la vista PC): los FPS del programa en primer plano, contados como PresentMon a partir
  de los eventos de presentación de ETW (Microsoft-Windows-DXGI 42/55, D3D9 1 y DxgKrnl con el filtro de eventos
  166/168/171/184/252); por proceso gana el tipo de evento más frecuente, y las apps Electron/Chromium se cuentan por su
  proceso hijo de GPU. La sesión de ETW solo existe mientras el anillo está a la vista y se muestrea una vez por
  segundo. Necesita la copia elevada (instalada); sin ella, o si nada presenta, enseña los FPS de composición del
  escritorio (`DwmGetCompositionTimingInfo`). El arco es FPS / frecuencia del monitor; la tarjeta enseña el proceso, la
  barra de FPS, el tiempo de fotograma y los Hz.
- **Modo juego** (anillo con un mando; vacío = apagado, lleno = encendido). Desactivado por defecto; se activa en la
  nueva página Ajustes → Modo juego, donde editas los programas que se cierran (por defecto: sincronización en la nube,
  Phone Link, Teams y los auxiliares de la Xbox Game Bar), los servicios que se detienen (por defecto WSearch,
  SysMain y DiagTrack), el plan de energía (Equilibrado por defecto, Alto rendimiento o No cambiarlo), si se
  desactivan las capturas de la Xbox Game Bar (valores de HKCU) y si al apagarlo se vuelven a abrir los programas
  cerrados, como usuario normal. Nunca cierra procesos protegidos ni del sistema, el proceso de la ventana en primer
  plano, el propio widget ni `claude.exe`, y hay una lista de servicios protegidos (seclogon, PawnIO…). El estado
  anterior se guarda en `gamemode.json`, en la carpeta de datos, antes de cada cambio; se restaura al salir y, si el
  equipo se apagó a medias, al volver a abrir el widget.
- **Varias cuentas por IA** (Claude, Codex y Cursor). En Ajustes → Agentes → Cuentas se añade otra cuenta con su carpeta
  de configuración (`CLAUDE_CONFIG_DIR` con `.credentials.json`, `CODEX_HOME` con `auth.json`, o el
  `--user-data-dir` de Cursor con `User\globalStorage\state.vscdb`) y un nombre. Sigue habiendo un anillo por IA y la
  tarjeta lleva pestañas de cuenta (Principal · Trabajo) para elegir cuál enseña el anillo; la elección se recuerda.
  Renovar la sesión con un clic en el anillo de Claude solo vale para la cuenta principal.
- **Cursor y OpenCode ya no se leen con permisos de administrador.** Antes el proceso elevado abría `state.vscdb` y
  `opencode.db` con SQLite. Ahora la app instalada arranca su propio ejecutable como usuario normal
  (`--read-database cursor|opencode <ruta|auto>`, con `__COMPAT_LAYER=RunAsInvoker`, oculto, dentro de un *job* que lo
  cierra y con un límite de 20 s): lee el archivo en solo lectura y en modo defensivo y escribe un JSON pequeño por una
  tubería. Cuesta un arranque corto por cada refresco de Cursor u OpenCode (~0,1 s de CPU y ~30 MB durante menos de
  un segundo) y nada entre refrescos. Las copias sin elevar (Debug) leen en el propio proceso.
- **SQLite sin Microsoft.Data.Sqlite.** Se usa SQLitePCLRaw 3.0.5 con el SQLite nativo **3.53.4** por su API directa
  (`UntrustedSqlite`), en lugar del 3.53.3 de la 2.1.0. Desaparecen también la referencia a
  `System.Security.Cryptography.ProtectedData` (viene con .NET 10) y `Microsoft.Data.Sqlite` de las librerías que se
  publican.
- **Actualizaciones con aviso y firma propia.** La comprobación automática viene **activada** por defecto (clave
  `checkUpdates`, que sustituye a `autoCheckUpdates`), al arrancar y cada 6 h. Una versión nueva pone un punto rojo en
  el botón de ajustes y un aviso en Ajustes («Nueva versión disponible · Descargar e instalar»); un clic descarga,
  verifica e instala, y el widget se reinicia. El flujo de Release firma ahora el ZIP con una clave ECDSA P-256 del
  proyecto (secreto `UPDATE_SIGNING_KEY`) y publica `OpenControlEdge-win-x64.zip.sig`: la app instala un archivo
  firmado si la firma coincide con la clave pública que lleva dentro (entonces no hacen falta las comprobaciones
  Authenticode ni por DLL; el recurso de versión del ejecutable debe seguir diciendo «Open Control Edge» y la
  versión). Sin `.sig`, sigue exigiendo la firma Authenticode de SignPath. **Quien tenga la 2.1.0 debe instalar la
  2.2.0 a mano una vez**: la 2.1.0 solo acepta actualizaciones firmadas por SignPath.
- **Feedback sin cuenta y con capturas.** Ajustes → Feedback → «Enviar» publica el mensaje en FormSubmit
  (formsubmit.co), que lo reenvía por correo al autor (su dirección queda detrás de un alias de FormSubmit), con hasta
  3 imágenes (PNG o JPEG de hasta 5 MB: «Capturar pantalla» fotografía la pantalla principal y «Añadir imagen…» elige
  un archivo) y un correo de contacto opcional para responderte. «Abrir en GitHub» abre un issue ya rellenado y copia al
  portapapeles la primera captura para pegarla.
- **Comprobación del sistema al instalar.** La ventana de instalación empieza con «Comprobando tu sistema…»: Windows
  10/11 de 64 bits, permisos de administrador, servicio Inicio de sesión secundario (se vuelve a activar si estaba
  desactivado), PawnIO (se instala solo con `-install`) e IA detectadas. Después, «Aceptar e instalar». Si PawnIO falla,
  la instalación sigue.
- **Ajustes y datos.** Nueva categoría «Modo juego» (ya son 7). La página Información enseña la versión mayor real de
  .NET.
- **.NET 10.** SDK 10.0.401 (`global.json`) y `net10.0-windows`. Los flujos de GitHub Actions pasan a versiones que
  corren en Node 24 (`actions/checkout@v7`, `actions/setup-dotnet@v6`, `actions/upload-artifact@v7` y
  `signpath/github-action-submit-signing-request@v2`) y el de Build solo se ejecuta en `main`.

## 2.1.0 — 2026-10-03

- **Tipografía Outfit** (400, 500 y 600; SIL OFL) en toda la app en lugar de Google Sans Flex, con cifras tabulares
  en los anillos para que no bailen.
- **Menos CPU en reposo.** Medido el 30-09-2026 en este portátil con la build Debug sin elevar, fuera de pantalla,
  10 min tras 4 de calentamiento (la temperatura de CPU no se lee sin elevar), CPU del proceso por minuto:

  | Panel | Antes | Después | Interfaz | Render | Fondo (red, sensores) |
  |---|---|---|---|---|---|
  | Fijado y visible | 0,389 s | **0,081 s** | 0,192 → 0,009 | 0,100 → 0,006 | 0,097 → 0,065 |
  | Plegado (automático) | 0,253 s | **0,111 s** | 0,117 → 0,006 | 0 → 0 | 0,136 → 0,104 |

  Cada lectura animaba anillos y barras durante 500–600 ms aunque estuvieran ocultos o no cambiaran, y cada fotograma
  repinta la ventana en capa entera: ahora solo se anima lo que está en pantalla y se mueve un 3 % o más. El sondeo
  del puntero (cada 150 ms) se detiene con el panel fijado en reposo, y lo reanuda el propio ratón sobre el panel,
  y con el panel plegado y el cursor quieto 2 s, y lo reanuda la entrada cruda del ratón, lápiz o pantalla táctil.
  Los refrescos de uso reutilizan las conexiones HTTPS en vez de negociar TLS cada vez (unos 80 ms de CPU menos por
  refresco). El icono de la bandeja no reenvía un texto que no ha cambiado. La RAM no baja: el montón gestionado
  ocupa 3 MB y el resto es WPF, el código nativo y LibreHardwareMonitor.
- Los textos de Ajustes pasan a los diccionarios `Strings.es.xaml` y `Strings.en.xaml`.
- «Iniciar con Windows» ya no se queda en «Comprobando…»: la consulta al Programador de tareas tiene 5 s de límite
  y, si falla, dice por qué.
- Claude con HTTP 429 o 5xx: se sigue mostrando la última lectura buena de menos de 15 min con «Dato de hace X min»
  y no se reintenta antes de 5 min. Sin sesión en Claude Code no se ejecuta la CLI: el anillo y «Conectar» abren
  `claude auth login` en una consola visible, como usuario normal.
- Tooltips del panel cortos («Liberar RAM»), interruptores de Ajustes sin parpadeos ni cambios solos, animación de
  cierre del panel (la inversa de la de apertura) y Ajustes como ventana normal, no siempre encima.
- Mensaje claro si falta el servicio Inicio de sesión secundario (seclogon); el mutex de instancia única ya no se
  puede bloquear desde otro proceso; al desinstalar, la carpeta de datos se devuelve al usuario; mensajes de
  servicios traducidos al inglés.
- **Actualizaciones desde la app, corregidas y endurecidas** (auditoría final antes de publicar):
  - El auxiliar que aplica la actualización arrancaba con su directorio de trabajo dentro de la carpeta que luego
    renombra, algo que Windows no permite: la actualización fallaba siempre y el widget se quedaba cerrado. Ahora
    arranca fuera, espera a que la app anterior se cierre y, si algo falla, vuelve a abrir la versión instalada.
  - El ZIP debe contener exactamente el ejecutable y sus 8 librerías nativas, ni un archivo más. Antes solo se
    comprobaba la firma del ejecutable y cualquier otra DLL del ZIP se habría instalado y cargado como administrador.
  - El ejecutable debe estar firmado por SignPath Foundation (nombre exacto, no «contiene») y su recurso de versión
    decir «Open Control Edge» y la versión anunciada. Cada librería debe ser la que trae la versión instalada o estar
    firmada por su editor (Microsoft, Xamarin o SignPath Foundation).
- **Instalación local**: el ejecutable lleva compilados los SHA-256 de sus 8 librerías nativas (se calculan al
  compilar a partir de los archivos que se publican) y el instalador solo copia librerías idénticas.
- Las bases SQLite de Cursor y OpenCode se abren en modo defensivo (solo lectura y consulta, `trusted_schema`
  desactivado, comprobación de celdas, sin mapear en memoria, solo archivos normales). Un proveedor oculto en
  Ajustes → Agentes ya no se lee nunca, tampoco al abrir esa página, que lo marca como «Oculto».
- Firma con SignPath Foundation preparada: el recurso de versión del ejecutable dice «Open Control Edge» y la versión
  sin sufijo de commit, la Release comprueba que la etiqueta coincide con la versión del proyecto, pasa esa versión
  a SignPath para que solo firme ese ejecutable y enlaza la política de firma.
- **SQLite nativo 3.53.3** (SQLitePCLRaw 2.1.13) en lugar del 3.41 que traía Microsoft.Data.Sqlite 8.0.11
  (SQLitePCLRaw 2.1.6), afectado por CVE-2025-6965.
- Las peticiones de uso (Claude, Codex, Cursor, DeepSeek, OpenRouter) ya no siguen redirecciones: la cookie de
  Cursor y las claves API van en cabeceras que HttpClient conserva al redirigir, también a otro dominio.
- El instalador comprueba los permisos de Program Files, de la carpeta y de cada archivo **antes** de sustituir la
  copia anterior, y cancela si alguien que no sea Administradores, SYSTEM o TrustedInstaller puede modificarlos
  (antes solo miraba tres grupos, la carpeta y el exe, y se limitaba a avisar). Los permisos de solo lectura y
  ejecución ya no cuentan como escritura.
- Pegar una clave API con espacios o saltos de línea ya no la invalida. Un valor de proveedor desconocido en el
  archivo de ajustes se ignora en vez de impedir que se guarde cualquier cambio.
- La X del panel dice «Salir de Open Control Edge». «Liberar RAM» informa solo de la memoria en uso liberada y ya
  no menciona una caché que no vacía.
- Limpieza: código muerto de la purga de la lista *standby*, textos y comentarios que ya no eran ciertos
  (cadencia «2 / 6 minutos», rutas de `%LOCALAPPDATA%` para la copia instalada), User-Agent con la versión real,
  verificación Authenticode unificada, lógica que dependía de comparar textos en español, y THIRD-PARTY-NOTICES
  completo (runtime de .NET y WPF, HidSharp, Mono.Posix, SQLitePCLRaw y las dependencias de LibreHardwareMonitor).

- **Escrituras elevadas protegidas.** Ajustes, registro, claves y caché del widget instalado se guardan en
  `%ProgramData%\OpenControlEdge\<SID>`, con la carpeta del producto y la del usuario endurecidas. Se elimina la
  copia automática de ajustes antiguos durante el arranque.
- **Instalador y actualizaciones verificadas.** La instalación copia el ejecutable y la lista cerrada de sus 8 bibliotecas
  nativas, nada más de la carpeta; el instalador de actualizaciones
  exige SHA-256 y una firma Authenticode válida de SignPath Foundation. Las descargas de actualización y PawnIO usan
  nombres de asset fijos; PawnIO exige el firmante fijado `CN=namazso.eu`.
- Los argumentos `--snapshot`, `--smoke-test` y `--test-update-fixture` solo se procesan en Debug.
  `--portable`, `--no-elevate` y «Usar sin instalar» se ignoran u ocultan cuando el proceso está elevado.
- Los enlaces, el Explorador y los logins se lanzan con el token del usuario. Feedback abre un formulario público vacío
  y copia el borrador para que se revise y edite antes de enviarlo.
- «Liberar RAM» está desactivado por defecto, solo recorta procesos de la sesión interactiva y ya no purga la lista
  standby.

Comportamiento nativo: sin scripts de PowerShell ni tareas hechas con scripts.

- **Instalación nativa.** Al abrir el ejecutable fuera de `C:\Program Files\OpenControlEdge` aparece la ventana
  «Instalar Open Control Edge»: con el único UAC copia el ejecutable y sus bibliotecas nativas (carpeta temporal,
  SHA-256 contra el original, cambio con vuelta atrás, comprobación de permisos; la firma Authenticode solo se exige
  a las actualizaciones descargadas), protege la carpeta de datos (sin enlaces, solo administradores,
  integridad alta), conserva la carpeta antigua EdgeWidget y registra el inicio con Windows en el
  Programador de tareas (al iniciar sesión, sin retraso, elevado, prioridad normal) y arranca la copia instalada.
  `--portable` solo tiene efecto cuando el proceso ya corre sin elevar. Menú de bandeja: «Iniciar con Windows» y «Desinstalar…».
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
  procesos accesibles de la sesión, sin purga standby), como mucho una vez por minuto; se activa con `ramCleanup`.
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
