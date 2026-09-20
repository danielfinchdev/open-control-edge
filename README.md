# EdgeWidget

**Un widget de escritorio para Windows 11 que muestra, pegado al borde de la pantalla, cuánto te queda
de cada IA y a qué temperatura está tu portátil.**

> *A Windows 11 edge widget showing your remaining Claude / Codex / Grok quota and your CPU & GPU
> temperature at a glance. Interface and documentation are in Spanish.*

Sin instalador, sin servicios en segundo plano, sin telemetría. Un único ejecutable que consume
**0,2 segundos de CPU por minuto** y no envía tus datos a ningún sitio.

![licencia](https://img.shields.io/badge/licencia-MIT-green) ![plataforma](https://img.shields.io/badge/Windows-11-blue) ![.NET](https://img.shields.io/badge/.NET-8-512BD4)

---

## Qué hace

Un panel negro anclado al borde derecho, siempre encima del resto de ventanas y sin botón en la barra
de tareas. Cada anillo es una fuente; al pasar el ratón por encima se despliega una tarjeta con el
detalle, que se desliza de un anillo a otro sin desaparecer.

| Anillo | Qué muestra |
|---|---|
| **Claude** | % de la sesión de 5 h, límite semanal y gasto acumulado en € |
| **Codex** | % de la ventana de límite, etiquetada por su duración (sesión / diario / semanal / mensual) |
| **Grok Bot** | % semanal y, opcionalmente, bajo demanda |
| **CPU** | Temperatura actual, máxima de la sesión y carga |
| **GPU** | Temperatura, uso y memoria (NVIDIA) |

Los anillos de Codex, Grok y GPU **aparecen y desaparecen solos** según haya o no datos que mostrar, y
el panel se recentra con una animación.

### Dos modos

- **Fijado** — el panel se queda siempre abierto.
- **Automático** — se recoge a una franja de 5 px en el borde y se despliega al acercar el ratón.

Se cambia con el botón del propio panel («Ocultar» / «Fijar») y la elección se recuerda.

### Detalles

- **Un clic en el anillo de Claude renueva la sesión** y abre Claude Code, para que el anillo no se
  quede en `--` cuando el token caduca.
- **Icono en la bandeja** con menú propio (Actualizar / Salir) e información al pasar el ratón.
- **Aviso de temperatura**: cada pico por encima de 90 °C queda anotado en el registro, incluso cuando
  el panel está recogido. Útil si tu portátil se calienta y no sabes cuándo.
- **Instancia única**, se recoloca solo al cambiar de monitor o de escala, y sobrevive a un reinicio
  del Explorador de Windows.

---

## Instalación

Requisitos: **Windows 11** (o 10 22H2) y permisos de administrador.

1. Descarga `EdgeWidget.exe` de la [última versión](../../releases/latest), o compílalo (ver abajo).
2. Ejecuta el instalador desde PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File tools\instalar.ps1
```

Copia el ejecutable a `C:\Program Files\EdgeWidget`, crea una tarea programada que lo arranca al
iniciar sesión como administrador, y lo lanza. **No borra nada.**

Para quitar el arranque automático:

```powershell
powershell -ExecutionPolicy Bypass -File tools\instalar.ps1 -Desinstalar
```

### ¿Por qué necesita administrador?

Leer la temperatura de la CPU requiere acceso a los registros MSR del procesador, y eso solo se puede
hacer con privilegios elevados. Es la única razón. Si prefieres no dárselos, la compilación en modo
Debug funciona sin elevar: verás todo menos la temperatura de la CPU.

---

## Configuración

`%LOCALAPPDATA%\EdgeWidget\EdgeWidget.settings.json`

```json
{
  "panelMode": "auto",
  "grok": {
    "weeklyPercent": 42,
    "weeklyResetsAt": "2026-09-22T09:00:00+02:00",
    "onDemandPercent": 12
  }
}
```

| Clave | Valores | Qué hace |
|---|---|---|
| `panelMode` | `"pinned"` \| `"auto"` | Modo del panel. Lo escribe el propio botón. |
| `grok.weeklyPercent` | 0–100 | **Obligatorio** para que aparezca el anillo de Grok. |
| `grok.weeklyResetsAt` | fecha ISO 8601 | Opcional. Pinta el «Reinicia el…» de la cabecera. |
| `grok.onDemandPercent` | 0–100 | Opcional. Añade una segunda barra a la tarjeta. |

El archivo se relee en cada actualización: puedes editarlo y pulsar «Actualizar» sin reiniciar nada.

---

## De dónde salen los datos

| Fuente | Origen | Credenciales |
|---|---|---|
| Claude | `api.anthropic.com/api/oauth/usage` | `%USERPROFILE%\.claude\.credentials.json` |
| Codex | `chatgpt.com/backend-api/wham/usage` | `%USERPROFILE%\.codex\auth.json` |
| Grok Bot | El archivo de ajustes | — |
| CPU / GPU | [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | — |

Los dos endpoints de uso **no están documentados** por sus proveedores. Los parsers exigen la forma
exacta de la respuesta: si cambia, el anillo muestra un error en vez de inventarse un número.

Grok Bot no tiene anillo automático porque un plan personal **no expone ningún endpoint de uso**, y su
consumo va contra una cuenta de Cursor cuya API de administración es solo para planes Teams. De ahí que
el porcentaje se escriba a mano.

---

## Privacidad y seguridad

Este programa lee archivos de credenciales. Merece que se explique exactamente qué hace con ellos.

**Qué lee, y solo eso**

- De `.credentials.json`: únicamente `claudeAiOauth.accessToken` y `expiresAt`.
- De `auth.json`: únicamente `tokens.access_token`.

Todo lo demás —**incluidos los tokens de refresco, el `id_token` y el `account_id`**— se salta a nivel
de lector JSON, sin llegar nunca a convertirse en una cadena de texto en memoria. Los archivos se abren
en **solo lectura** y no se escriben jamás. El búfer se limpia con `Array.Clear` al terminar de leer.

**Qué no hace**

- No renueva tokens. Si el token de acceso está caducado, ni siquiera envía la petición: muestra
  «Abre Claude Code para renovar». La renovación la hace un script aparte, como usuario normal.
- No escribe secretos en el registro. Solo códigos de estado HTTP y mensajes propios; nunca cuerpos de
  respuesta ni cabeceras.
- No envía nada a ningún sitio salvo a los dos endpoints de la tabla de arriba, por HTTPS.
- No tiene telemetría, ni analítica, ni actualizaciones automáticas, ni servicios residentes.

**El ejecutable vive en `C:\Program Files`, a propósito**

La tarea programada lo arranca como administrador sin pedir UAC. Si el ejecutable estuviese en una
carpeta donde tu usuario puede escribir, cualquier programa que se ejecutase con tu cuenta —sin ser
administrador— podría sustituirlo y heredar esos permisos en el siguiente inicio de sesión. En
`C:\Program Files` solo un administrador puede escribir. El instalador verifica los permisos y avisa si
no son correctos.

**Lo que queda, dicho claramente**

- El widget corre como administrador y maneja tokens OAuth. Es una superficie de ataque mayor que la de
  un programa normal. Está mitigado con lo anterior, no eliminado.
- LibreHardwareMonitor carga un controlador de kernel (PawnIO) mientras el widget está abierto. Es el
  mecanismo estándar para leer sensores en Windows, pero es código de kernel de terceros.
- El ejecutable no está firmado: SmartScreen puede avisar la primera vez.

---

## Mantener viva la sesión de Claude

El token de acceso de Claude Code dura unas 8 horas. Cuando caduca, el anillo se queda en `--`.

Lo que **no** lo renueva: abrir la aplicación de escritorio de Claude (guarda su sesión en otro sitio),
ni `claude auth status` (solo informa). Lo único que lo renueva es una llamada real a la API, que hace
que la CLI use el token de refresco y reescriba el archivo.

`tools\claude-sesion.ps1` automatiza eso:

```powershell
# Instalar (no necesita administrador)
powershell -ExecutionPolicy Bypass -File tools\claude-sesion.ps1 -Instalar -AbrirApp

# Renovar ahora mismo
powershell -ExecutionPolicy Bypass -File tools\claude-sesion.ps1 -Forzar
```

- Se dispara al iniciar sesión **con 3 minutos de retraso** y después **cada 4 horas**.
- Solo llama a la API si al token le quedan menos de 2 horas. Si le queda margen, no hace nada.
- La llamada es la mínima posible: `claude -p --tools "" --no-session-persistence --model haiku ok`.
- Deja constancia en `tools\claude-sesion.log`. **Nunca escribe ningún token**, solo fechas.

El clic en el anillo de Claude lanza esta misma tarea.

---

## Compilar

Requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/danielfinchdev/EdgeWidget.git
cd EdgeWidget
dotnet publish src\EdgeWidget\EdgeWidget.csproj -c Release -r win-x64 -o dist
powershell -ExecutionPolicy Bypass -File tools\instalar.ps1
```

**Release** genera un único archivo autocontenido de ~65 MB (no hace falta tener .NET instalado) y
exige administrador. **Debug** arranca sin elevar, para iterar la interfaz sin UAC.

### Revisar la interfaz sin ejecutar el widget

```powershell
.\src\EdgeWidget\bin\Debug\net8.0-windows\win-x64\EdgeWidget.exe --snapshot C:\temp\capturas
```

Renderiza **15 PNG** con todos los estados —los dos modos, las cinco tarjetas, los errores, los anillos
ocultos, los botones en hover y el menú de bandeja— y sale. No lee credenciales, no toca los ajustes y
no abre los sensores. Es la forma de revisar un cambio visual en segundos.

---

## Cómo está hecho

**C# 12 / .NET 8 / WPF.** Sin frameworks de interfaz, sin MVVM, sin inyección de dependencias: una
ventana, unos cuantos controles dibujados a mano y llamadas directas a la API de Windows.

```
src/EdgeWidget/
├─ Services/     Lectura de credenciales, APIs de uso, sensores, ajustes, registro
├─ Ui/           Anillo, barra, iconos, paleta, formatos
├─ Views/        Ventana del borde, menú de bandeja, renderizador de capturas
└─ Interop/      user32 / shell32: bandeja, posición, clic-a-través
```

Algunas decisiones que quizá no son obvias:

- **Una sola ventana** contiene la franja, el panel y la tarjeta. Así la tarjeta puede deslizarse de un
  anillo a otro con animaciones de render en vez de mover ventanas del sistema.
- **El hover se detecta sondeando la posición del cursor**, no con eventos: cuando el panel está
  recogido la ventana es transparente a los clics (`WS_EX_TRANSPARENT`) y no recibe ratón en absoluto.
- **Ese sondeo va en dos velocidades.** La ventana guarda su rectángulo en píxeles y cada vuelta empieza
  por una comparación de cuatro enteros: si el cursor está fuera (lo normal), 150 ms y cero trabajo de
  WPF; si está encima, 30 ms. Es la diferencia entre gastar un 2 % de un núcleo todo el día y gastar un
  0,35 %.
- **Las cadencias dependen de si el panel está a la vista**: sensores cada 20 s / 60 s, uso cada 2 / 6
  minutos. Los sensores nunca se paran del todo, para que «Máxima de la sesión» sea una cifra real y no
  solo los momentos en que estabas mirando.
- **Los anillos y las barras son `FrameworkElement` con `OnRender`**, no plantillas de control. Menos
  árbol visual y control exacto del trazo.
- **Los iconos están dibujados en código** en un espacio de 24×24, no son fuentes ni SVG.
- **El icono de bandeja usa `Shell_NotifyIcon` directamente**, sin WinForms, con los filtros UIPI que
  hacen falta para que un proceso elevado reciba los clics de un Explorer de integridad media.

---

## Licencia

[MIT](LICENSE). Úsalo, cámbialo y publícalo como quieras.

---

## Créditos

Sensores: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).

Los iconos son glifos genéricos dibujados para este proyecto, no logotipos de marca. EdgeWidget no está
asociado con Anthropic, OpenAI ni xAI.
