# Open Control Edge (antes EdgeWidget)

**Un widget de escritorio para Windows 11 que muestra, pegado al borde de la pantalla, cuánto te queda
de cada IA y a qué temperatura está tu portátil.**

> *A Windows 11 edge widget showing your remaining Claude / Codex / Cursor quota and your CPU & GPU
> temperature at a glance. Interface and documentation are in Spanish.*

Sin instalador, sin servicios en segundo plano, sin telemetría. Un único ejecutable que consume
**0,2 segundos de CPU por minuto**. Solo consulta los endpoints documentados abajo para actualizar tarjetas y lee los datos de OpenCode localmente.

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
| **Cursor** | % del ciclo mensual Pro y, si existe, bajo demanda |
| **OpenCode** | Tokens de entrada / salida / razonamiento / caché y coste acumulado local |
| **DeepSeek** | Saldo API restante en la moneda devuelta por el proveedor |
| **OpenRouter** | % usado si la clave tiene límite; si no, gasto acumulado en USD |
| **CPU** | Temperatura actual, máxima de la sesión y carga |
| **GPU** | Temperatura, uso y memoria (NVIDIA) |

Los anillos de OpenCode aparecen si se detecta su CLI o sus datos locales; DeepSeek y OpenRouter aparecen al guardar una clave.
Claude, Codex y Cursor se detectan por sus clientes o credenciales. Puedes forzar u ocultar cada anillo en ajustes.
El anillo de GPU sigue ocultándose cuando no hay NVIDIA detectada. El panel se recentra con una animación
al mostrar u ocultar anillos.

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

1. Descarga `OpenControlEdge.exe` de la [última versión](../../releases/latest), o compílalo (ver abajo).
   Los ejecutables de Releases los publica [GitHub Actions](../../actions) a partir de este código.
   Cuando el flujo de Release publique attestations, se pueden verificar con
   `gh attestation verify OpenControlEdge.exe -R danielfinchdev/open-control-edge`.
2. Ejecuta el instalador desde PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File tools\instalar.ps1
```

Copia el ejecutable a `C:\Program Files\OpenControlEdge`, crea una tarea programada que lo arranca al
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

`%LOCALAPPDATA%\OpenControlEdge\OpenControlEdge.settings.json`

```json
{
  "panelMode": "auto",
  "providers": {
    "claude": "auto",
    "codex": "auto",
    "cursor": "auto",
    "opencode": "auto",
    "deepseek": "auto",
    "openrouter": "auto"
  }
}
```

| Clave | Valores | Qué hace |
|---|---|---|
| `panelMode` | `"pinned"` \| `"auto"` | Modo del panel. Lo escribe el propio botón. |
| `providers` | objeto opcional | Visibilidad de cada anillo de IA (ver abajo). |

Cada clave dentro de `providers` (`claude`, `codex`, `cursor`, `opencode`, `deepseek`, `openrouter`) admite:

| Valor | Qué hace |
|---|---|
| `"auto"` | Muestra el anillo solo si el cliente está instalado (valor por defecto). |
| `"show"` | Fuerza el anillo aunque no se detecte la instalación (sin llamar a la API si no hay cliente). |
| `"hide"` | Oculta el anillo aunque el cliente esté instalado. |

Si omites `providers` o una clave concreta, se usa `"auto"`. El widget no escribe `providers` hasta que
los edites tú; al cambiar el modo del panel se conservan las claves que ya tuvieras.

---

## De dónde salen los datos

| Fuente | Origen | Credenciales |
|---|---|---|
| Claude | `api.anthropic.com/api/oauth/usage` | `%USERPROFILE%\.claude\.credentials.json` |
| Codex | `chatgpt.com/backend-api/wham/usage` | `%USERPROFILE%\.codex\auth.json` |
| Cursor | `cursor.com/api/usage-summary` | `%APPDATA%\Cursor\User\globalStorage\state.vscdb` |
| OpenCode | Base local SQLite `opencode*.db` | CLI o carpeta de datos local |
| DeepSeek | `api.deepseek.com/user/balance` | Clave DPAPI CurrentUser |
| OpenRouter | `openrouter.ai/api/v1/key` | Clave DPAPI CurrentUser |
| CPU / GPU | [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | — |

Los endpoints de Claude, Codex y Cursor **no están documentados** por sus proveedores. Los parsers exigen la forma
exacta de la respuesta: si cambia, el anillo muestra un error en vez de inventarse un número.

---

## Privacidad y seguridad

Este programa lee archivos de credenciales. Merece que se explique exactamente qué hace con ellos.

**Qué lee, y solo eso**

- De `.credentials.json`: únicamente `claudeAiOauth.accessToken` y `expiresAt`.
- De `auth.json`: únicamente `tokens.access_token`.
- De `state.vscdb`: únicamente `cursorAuth/accessToken` y `cursorAuth/stripeMembershipType`; la base se abre en solo lectura.
- Cursor envía la sesión en la cookie de la petición HTTPS a `cursor.com`; nunca la guarda ni la registra.

Todo lo demás —**incluidos los tokens de refresco, el `id_token` y el `account_id`**— se salta a nivel
de lector JSON, sin llegar nunca a convertirse en una cadena de texto en memoria. Los archivos se abren
en **solo lectura** y no se escriben jamás. El búfer se limpia con `Array.Clear` al terminar de leer.

**Qué no hace**

- No renueva tokens. Si el token de acceso está caducado, ni siquiera envía la petición: muestra
  «Abre Claude Code para renovar». La renovación la hace un script aparte, como usuario normal.
- No escribe secretos en el registro. Solo códigos de estado HTTP y mensajes propios; nunca cuerpos de
  respuesta ni cabeceras.
- Solo envía peticiones HTTPS a los endpoints de Claude, Codex, Cursor, DeepSeek y OpenRouter indicados en este README. OpenCode no usa red.
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

El clic en el anillo de Claude lanza esta misma tarea. Si no está instalada, la tarjeta lo dice y solo se
abre Claude Code (que no renueva el token).

---

## Compilar

Requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/danielfinchdev/open-control-edge.git
cd open-control-edge
dotnet publish src\OpenControlEdge\OpenControlEdge.csproj -c Release -r win-x64 -o dist
powershell -ExecutionPolicy Bypass -File tools\instalar.ps1
```

**Release** genera un único archivo autocontenido de ~65 MB (no hace falta tener .NET instalado) y
exige administrador. **Debug** arranca sin elevar, para iterar la interfaz sin UAC.

Cada etiqueta `v*` (por ejemplo `v1.2.0`) lanza el mismo `dotnet publish` en
[Actions](../../actions) y adjunta `OpenControlEdge.exe` a la [Release](../../releases):

```powershell
git tag v1.2.0
git push origin v1.2.0
```

### Revisar la interfaz sin ejecutar el widget

```powershell
.\src\OpenControlEdge\bin\Debug\net8.0-windows\win-x64\OpenControlEdge.exe --snapshot C:\temp\capturas
```

Renderiza **28 PNG** con los dos modos, las ocho tarjetas, estados de error y sin datos, anillos ocultos, menú de bandeja y diálogo de claves. Incluye una prueba del parser de OpenCode con JSON de ejemplo. No lee credenciales, no toca los ajustes ni abre los sensores.
---

## Cómo está hecho

**C# 12 / .NET 8 / WPF.** Sin frameworks de interfaz, sin MVVM, sin inyección de dependencias: una
ventana, unos cuantos controles dibujados a mano y llamadas directas a la API de Windows.

```
src/OpenControlEdge/
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

Los iconos son glifos genéricos dibujados para este proyecto, no logotipos de marca. OpenControlEdge no está
asociado con Anthropic, OpenAI ni xAI.

## Proveedores añadidos y claves de API

| IA | Fuente y métrica | Estado |
|---|---|---|
| OpenCode | Base local `opencode*.db`: tokens de entrada/salida/razonamiento/caché y coste | Implementado; no usa red |
| DeepSeek | [`GET https://api.deepseek.com/user/balance`](https://api-docs.deepseek.com/api/get-user-balance/): saldo en CNY o USD | Implementado |
| Perplexity | No encontré una API pública oficial de uso o saldo | No implementado |
| GitHub Copilot | [API de facturación de GitHub](https://docs.github.com/en/rest/billing/usage): créditos consumidos; las peticiones premium solo aplican al modelo heredado | No implementado: no proporciona una cuota de peticiones universal para todas las cuentas |
| OpenRouter | [`GET https://openrouter.ai/api/v1/key`](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key): uso y límite de la clave | Implementado |
| Gemini CLI | `/stats`: estadísticas de sesión, no cuota persistente | No implementado |
| Mistral | [Admin API de uso](https://docs.mistral.ai/admin/admin-api/usage-metrics): consumo/coste de organización | Requiere clave admin y alcance de organización |
| xAI | [Management API](https://docs.x.ai/developers/rest-api-reference/management/billing): uso del equipo | Requiere permisos y equipo |
| Windsurf | Endpoint de cuota observado por terceros, no documentado oficialmente | No implementado |

### Cómo guardar o borrar claves

En el menú de la bandeja, selecciona **Claves de API…**. El diálogo tiene un `PasswordBox` para DeepSeek y otro para OpenRouter, con botones **Guardar** y **Borrar**. También se abre desde consola con:

```powershell
OpenControlEdge.exe --set-key deepseek
OpenControlEdge.exe --set-key openrouter
```

Las claves se cifran con Windows DPAPI en ámbito `CurrentUser` y se guardan como blobs binarios en `%LOCALAPPDATA%\OpenControlEdge\keys\deepseek.bin` y `openrouter.bin`. Nunca se guardan en el JSON de settings ni se escriben en el registro. Se descifran solo en memoria para formar la cabecera HTTPS.

### Datos locales de OpenCode

El detector busca `opencode` en `PATH` o el directorio de datos de OpenCode. Se respetan `OPENCODE_DB` y `XDG_DATA_HOME`; también se inspeccionan las ubicaciones de datos habituales de Windows. El lector abre cada `opencode*.db` con SQLite en solo lectura y agrega solamente columnas de uso de la tabla `session`. No consulta la CLI para obtener los datos y no crea conexiones de red.

La prueba del parser incluida en `--snapshot` usa este objeto **normalizado interno**; no representa una salida prometida de `opencode stats --json`:

```json
{
  "totalTokens": {
    "input": 12345,
    "output": 6789,
    "reasoning": 321,
    "cache": { "read": 2100, "write": 55 }
  },
  "totalCost": 1.2345
}
```

La tarjeta muestra los cinco contadores y el coste registrado. Si falta la tabla o cualquier campo esperado, indica error en lugar de calcular una cifra aproximada.

### Destinos de red y seguridad

Además de Claude, Codex y Cursor, el widget contacta estos destinos solo para actualizar las tarjetas:

- `https://api.deepseek.com/user/balance` (DeepSeek; requiere la clave cifrada del usuario).
- `https://openrouter.ai/api/v1/key` (OpenRouter; requiere la clave cifrada del usuario).

OpenCode es local. Perplexity y Windsurf no se contactan. Las cabeceras y los cuerpos de respuesta nunca se registran; los servicios muestran errores propios y tienen timeout de 15 segundos.
