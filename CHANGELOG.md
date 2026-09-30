# Registro de cambios

## 2.1.0 — sin publicar

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
    decir OpenControlEdge y la versión anunciada. Cada librería debe ser la que trae la versión instalada o estar
    firmada por su editor (Microsoft, Xamarin o SignPath Foundation).
- **Instalación local**: el ejecutable lleva compilados los SHA-256 de sus 8 librerías nativas (se calculan al
  compilar a partir de los archivos que se publican) y el instalador solo copia librerías idénticas.
- Las bases SQLite de Cursor y OpenCode se abren en modo defensivo (solo lectura y consulta, `trusted_schema`
  desactivado, comprobación de celdas, sin mapear en memoria, solo archivos normales). Un proveedor oculto en
  Ajustes → Agentes ya no se lee nunca, tampoco al abrir esa página, que lo marca como «Oculto».
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
