# ParentCLT — Plan y Estado del Proyecto

> Documento de retoma. Un agente nuevo DEBE leer esto primero y seguir la sección "TAREA DE MAÑANA" para continuar sin depender de la sesión anterior.

## Qué es
ParentCLT = control parental con filtro DNS local:
- **Agente Windows** (`ParentCLT.Agent`) — servicio Windows que redirige el DNS de todas las NIC a `127.0.0.1` y levanta un mini-servidor DNS que aplica una blacklist; envía heartbeats al servidor y recibe política (DNS/blacklist/poll interval).
- **Servidor** (`web-parent-clt`, Laravel 11 en WSL) — API `api/v1` para register, policy (ETag/304) y heartbeat.
- **Watchdog** — tarea programada (`ParentCLT Watchdog`, cada 5 min, SYSTEM) que reactiva el servicio.
- **Instalador** — Inno Setup (`ParentCLT-Setup.exe`) que despliega agente + watchdog + servicio, y al desinstalar restaura servicio/DNS/Datos.

## Estructura de directorios (rutas REALES verificadas)
- Repo: `C:\Users\Unicomfacauca\parent-clt\ParentCLT`
- Agente (C#/.NET 10, TFM `net10.0-windows`, self-contained):
  - `src\ParentCLT.Agent\` (proyecto `ParentCLT.Agent.csproj`)
  - Código clave:
    - `Services\DnsManager.cs` — aplica/restaura filtro DNS vía WMI (`SetDNSServerSearchOrder`/`SetDNSServerSearchOrder`), persiste `OriginalDns`
    - `Services\ConfigStore.cs` — carga/guarda `state.json` desde `%ProgramData%\ParentCLT\state.json`
    - `Services\PolicyWorker.cs` — ciclo: register → fetch policy → apply → heartbeat
    - `Services\DnsFilterServer.cs` — mini servidor local
  - appsettings: `Installer\appsettings.template.json` (placeholders `__SERVER_URL__`, `__DEVICE_NAME__`)
- Servidor (WSL): `/home/peter/dev/web-parent-clt` (según lo indicado por el usuario; verificar al retomar)

## M5 (última iteración): DESINSTALACIÓN — HECHO Y VERIFICADO (18-Sep-2026)

Ejecuté el ciclo completo en la máquina de prueba (NO la VM limpia):

1. **Desinstalación correcta**: servicio eliminado, `C:\Program Files\ParentCLT` y `%ProgramData%\ParentCLT` borradas, watchdog (tarea + bat) eliminado. Setup `.exe` se conserva (comportamiento correcto de Inno).

2. **BUG REAL detectado**: al desinstalar, el DNS quedó en `127.0.0.1` (no se restauró el original). `restore-dns` imprimió *"No hay DNS original guardado que restaurar"*. Diagnóstico: en `state.json` quedó `OriginalDns: []` (vacío) con `DnsFilterApplied: true`.
   - Causa raíz **en el agente**, no en el instalador: al aplicar el filtro, si el adaptador **ya apuntaba a 127.0.0.1** (re-aplicación por watchdog/policy refresh en el mismo ciclo), el código sobrescribía `OriginalDns` con el valor filtrado (`["127.0.0.1"]`), o lo dejaba vacío — perdiendo el DNS original.

3. **CORRECCIÓN APLICADA** (`Services\DnsManager.cs`, `ApplyLocalFilter` — guard de re-aplicación):
   - Si el adaptador ya está en `127.0.0.1` (`isAlreadyFiltered`), YA NO sobrescribir `OriginalDns`: reutiliza el ya persistido en `state.json` (vía `ConfigStore`), de modo que el DNS original nunca se pierde por re-aplicaciones.
   - Solo persiste `OriginalDns` una vez (primer ciclo que realmente cambia de un DNS != 127.0.0.1).
   - `RestoreOriginalDns` ya restaura desde `state.json` (guard: `OriginalDns is not { Count: > 0 }` → log y return).

4. **REPUBLICADO del instalador con el fix embebido**:
   - Compilado el agente → publish en `artifacts\publish\` (lo que consume el ISS).
   - `ISCC.exe "Installer\installer.iss"` → OK → `Installer\Output\ParentCLT-Setup.exe` (26,198,529 bytes / ~26 MB, timbre `18/09/2026`).

5. **Equipo de prueba dejado SANO**: DNS restaurado a `8.8.8.8`/`1.1.1.1` manualmente (la prueba no restore-ó por el bug, pero no debe quedar contaminando).

## TAREA DE MAÑANA (VM limpia) — VALIDACIÓN FINAL
1. En la VM limpia (Windows con 1+ NIC activa), ejecutar `ParentCLT-Setup.exe` (el republicado con el fix de M5).
   - Pasa la página de URL/dispositivo o usa `/SERVERURL=... /DEVICENAME=... /SILENT`.
2. Verificar:
   - Servicio `ParentCLT Agent` RUNNING.
   - `%ProgramData%\ParentCLT\state.json` contiene `OriginalDns` **no vacío** con los DNS reales del adaptador (8.8.8.8/1.1.1.1 u otros) ANTES de que el filtro se aplique.
   - DNS de la NIC = `127.0.0.1` (filtro activo).
   - Tarea `ParentCLT Watchdog` existe.
3. **Desinstalar** (`...\ParentCLT\Unins...` o desde Panel → Programas).
4. VERIFICAR EL FIX: el DNS debe **volver al original** (8.8.8.8/1.1.1.1), NO quedar en 127.0.0.1.
5. Confirmar servicio + watchdog + carpetas borrados tras desinstalación.

## Comandos útiles (Windows / PowerShell)
- Publicar agente: `dotnet publish src\ParentCLT.Agent\ParentCLT.Agent.csproj -c Release -o artifacts\publish`
- Reproducir setup: `& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" "Installer\installer.iss"`
- Estado servicio: `sc.exe query "ParentCLT Agent"`
- Estado tarea: `schtasks /Query /TN "ParentCLT Watchdog"` (requiere elevación si se creó con `/RL HIGHEST`)

## Validación M6 desde otra PC (configurada 19-Sep-2026)
- Servidor WSL corre en `0.0.0.0:8899` (tmux `parentclt`).
- **Portproxy** (admin): `netsh interface portproxy add v4tov4 listenaddress=0.0.0.0 listenport=8899 connectaddress=192.168.239.215 connectport=8899` + regla firewall `ParentCLT WSL 8899` (TCP in, allow).
- **⚠️ La IP de WSL cambia al reiniciar WSL/Windows** → si la prueba falla por conexión, re-ejecutar el portproxy con la IP nueva (`wsl hostname -I`).
- **⚠️ El IP LAN de este host es DHCP**: hoy es `192.168.1.7` (Wi-Fi; la Ethernet está sin cable). Si cambia, ver con `ipconfig`.
- URL para el agente en la PC de pruebas: `http://192.168.1.7:8899`
- Setup en la PC de pruebas (cmd admin): `ParentCLT-Setup.exe /SERVERURL=http://192.168.1.7:8899 /DEVICENAME=PC-Pruebas /SILENT`

## Validación M6 en PC física — resultados (19-Sep, en curso)
- **INSTALACIÓN OK** en `2doPiso-Portatil` (SAC de Win11 bloqueó el setup unsigned → desactivado Smart App Control; exclusión Defender aplicada). Servicio RUNNING, dispositivo online en panel.
- **Filtro por franja OK**: ventana temporal sábado 12:37-13:47 (hora local) perfil estudio → NIC DNS = `127.0.0.1`, `instagram.com` → NXDOMAIN ✓.
- **Lección de timezone**: el servidor (PHP) corre en UTC y los dispositivos en UTC-5 → las franjas se evalúan con el RELOJ LOCAL del dispositivo (diseño). Las ventanas del panel son "hora local del dispositivo". Pendiente: hint en la UI de Programación.
- **BUG FIX (bucle DoH bootstrap)**: upstream era `cloudflare-dns.com` (dominio) → el agente lo resolvía con el DNS del sistema = 127.0.0.1 = él mismo → bucle al reconectar TLS (google.com dio TIMEOUT en el portátil). FIX: upstream por IP literal `https://1.1.1.1/dns-query` (sin lookup, sin SNI que bloquear). Cambiado en template + default del código + setup republicado.
- **BUG CRÍTICO REDESCUBIERTO — restore en desinstalación falló OTRA VEZ en el portátil** (DNS quedó 127.0.0.1 tras desinstalar con filtro activo). Causa más probable: carrera — `sc stop` es asíncrono y el servicio/watchdog pueden re-aplicar el filtro después del restore (también: `RestoreOriginalDns` hacía `Clear()` del original aunque el WMI devolviera error). **HARDENING aplicado**: (1) uninstaller ahora hace `taskkill /F /IM ParentCLT.Agent.exe /T` antes del restore (ningún proceso vivo puede re-aplicar); (2) `RestoreOriginalDns` solo borra los originales que restauraron con código 0 (los fallidos quedan para reintento). Setup republicado.
- Pendiente de validar: reinstalar con setup nuevo → filtro + DoH OK → desinstalar → confirmar restauración DNS. Si vuelve a fallar: revisar Event Viewer (Application, source ParentCLT.Agent) del portátil.
- **RESTAURACIÓN DNS POR DHCP (19-Sep)**: `OriginalDns` ahora registra si el DNS del adaptador venía por DHCP (registry `NameServer` vacío) → al restaurar, `SetDNSServerSearchOrder(null)` devuelve el adaptador a automático (DHCP) en vez de fijar IPs guardadas. Estáticos se restauran como antes. Sobrevive a cambios de red/router.
- **TESTBOX-01** (esta PC dev) también corre el agente con la franja activa — OJO: su servicio en memoria es el binario de AYER (sin schedule); reiniciar servicio o reinstalar para actualizar.

## Notas / advertencias
- El ISS empaqueta desde `artifacts\publish` → SIEMPRE publicar ahí antes de ISCC para que el setup lleve la última corrección.
- `OriginalDns` se persiste en `state.json` (no en servicio); el watchdog re-aplica el filtro leyendo ese mismo archivo → por eso el guard de no-sobrescribir es crítico.
- Si al desinstalar el DNS quedara en 127.0.0.1 de nuevo, revisar `state.json` antes de borrarlo (vuelve a fallar solo si `OriginalDns` está vacío).

## M7: Perfiles de bloqueo programables (hecho 19-Sep-2026)

**Qué es**: perfiles (listas de bloqueo nombradas: estudio, sábados, domingos, entretenimiento, dormir) + programación semanal por franjas horarias, con programación por defecto y overrides por dispositivo (borrar/reemplazar una franja concreta para un dispositivo).

**Web** (`/home/peter/dev/web-parent-clt`, WSL):
- Tablas nuevas (migraciones `2026_09_19_0300xx`):
  - `blacklist_profiles` (name, slug, description, dns_mode local-filter|off) — el perfil ES la lista; "dormir" = dns_mode `off` sin reglas (sin filtro).
  - `blacklist_profile_rules` (dominios del perfil, exact/wildcard).
  - `schedule_windows` (franjas por defecto: day_of_week 0=Dom..6=Sáb, start_time, end_time, profile_id; NO cruzar medianoche).
  - `schedule_overrides` (device_id + window_id + action delete|replace + profile_id).
- `PolicyService::buildForDevice` emite `schedule[]` (ventanas efectivas: defaults − overrides delete, con replace resueltos) + `settings.schedule_eval_sec:30`.
- Seeder `ParentScheduleSeeder` (idempotente): 5 perfiles + agenda ejemplo L-V 7-16 estudio, 16-17 entretenimiento, 18-21 estudio, dormir diario 22:00-23:59 y 00:00-05:00. Ejecutar: `php artisan db:seed --class=ParentScheduleSeeder --force`.
- Admin UI: `ProfileController`, `ScheduleController`, `DeviceScheduleController` + vistas `profiles/*`, `schedules/index`, `devices/schedule`; nav "Perfiles"/"Programación"; link "Programación" en cada dispositivo. Cada cambio hace `bumpAllDevices()`.
- **Semántica**: con schedule, el agente aplica SOLO el perfil de la ventana activa (ignora la blacklist heredada); sin ventana activa → modo `off` (DNS original, sin filtro); sin schedule → comportamiento heredado.

**Agente** (Windows):
- `ConfigStore.cs`: DTOs `PolicyScheduleDto`/`PolicyScheduleProfileDto`, `SettingsDto.ScheduleEvalSec`, `AgentState.Schedule` (persistido en state.json → funciona offline).
- `PolicyWorker.cs`: tick cada `schedule_eval_sec` (30 s) que evalúa `DateTime.Now` local (dow + franja) y aplica el perfil activo (`EvaluateScheduleTick`/`SetScheduledMode`, solo re-aplica si cambia, key=`schedule:<perfil>:<nreglas>`); `EnsureDnsAsync` cede cuando hay schedule; sync HTTP sigue cada `poll_interval` con ETag.

**Verificado**: migraciones + seeder OK; 26 tests del servidor pasan; 4 vistas renderizan; API real HTTP 200 con 29 ventanas; agente compila 0 errores; `ParentCLT-Setup.exe` republicado con soporte de schedule.

**M7b — modo `block-all` (19-Sep-2026)**: el perfil "dormir" quedó configurado en `dns_mode = block-all`: mientras esa ventana está activa, el agente responde NXDOMAIN a TODAS las consultas DNS (internet sin salida, aún con filtro local ON). Agregado a: migración `030005` (enum), `ProfileController` (validación), `PolicyService::DNS_MODES`, selects de las vistas de perfiles (badge rosa), y agente (`DnsFilterServer.BlockAll` + manejo en `SetScheduledMode`/`ApplyPolicy`; la key de re-aplicación incluye el modo para detectar el cambio off→block-all). Útil también para cualquier otro perfil "castigo".

**M7c — perfil `castigo` (19-Sep-2026)**: perfil adicional `castigo` (block-all, sin reglas ni ventanas) creado en BD y en el seeder. Uso: override "Reemplazar por… castigo" sobre cualquier franja en la programación de un dispositivo, o crear una franja nueva con ese perfil. Seeder y BD alineados (dormir=block-all); política bump a v2.

**M7d — descarga del instalador desde el panel + FIX CRÍTICO self-contained (19-Sep-2026)**:
- **FIX**: `installer.iss` solo empacaba `ParentCLT.Agent.exe` (apphost) — el setup de 2,1 MB era framework-dependent y NO funcionaría en máquinas sin .NET. Corregido: `[Files] Source: "..\artifacts\publish\*"` con `recursesubdirs` → publish self-contained completo (`-r win-x64 --self-contained true`, 79,5 MB) → setup final **26,8 MB**. Desde ahora: limpiar `artifacts\publish` antes de republicar (evita mezclar binarios viejos).
- **Descarga web**: `GET /downloads/setup` (auth) en `DownloadController` sirve DIRECTO el exe de `/mnt/c/Users/Unicomfacauca/parent-clt/ParentCLT/Installer/Output/ParentCLT-Setup.exe` → siempre la última compilación sin copiar nada. Sin sesión → redirect a login. Botón de descarga en el dashboard. Test `DownloadTest` (28 tests total en verde).
