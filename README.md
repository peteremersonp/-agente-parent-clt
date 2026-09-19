# ParentCLT Agent (Windows)

Agente de control parental para Windows 10/11. Redirige el DNS de todos los
adaptadores activos a `127.0.0.1`, levanta un mini-servidor DNS filtrador en ese
puerto y aplica **perfiles de bloqueo programados por franjas horarias** según la
política que entrega el servidor web (Laravel, repo hermano `web-parent-clt`).

> Complemento de este proyecto: **servidor Laravel** (`web-parent-clt`) con panel
> de administración, perfiles, programación semanal y descarga del instalador.

## Cómo funciona

```
PolicyWorker (BackgroundService)
  ciclo cada 60 s:
    1. Si no hay machine_id/token -> POST /api/v1/devices/register
    2. GET /api/v1/policy/{machine_id} (If-None-Match -> 304)
    3. Cada 30 s (schedule_eval_sec): evaluar la ventana activa con el reloj local
    4. Heartbeat -> POST /api/v1/heartbeat
```

- **DnsFilterServer** (`127.0.0.1:53`): responde NXDOMAIN a lo bloqueado y reenvía
  el resto por **DoH a `https://1.1.1.1/dns-query`** (IP literal: sin bucle de
  bootstrap de DNS y sin bloqueo SNI del ISP).
- **DnsManager** (WMI): aplica/restaura el DNS del equipo. Restaura **a automático
  (DHCP)** los adaptadores que tenían DNS por DHCP, y los estáticos con sus
  servidores guardados. Nunca guarda `127.0.0.1` como "original".
- **ConfigStore**: persiste todo en `%ProgramData%\ParentCLT\state.json`
  (funciona offline: mantiene la última política y el schedule).

### Perfiles programados

El payload de política incluye `schedule`: una lista de ventanas
`(day_of_week 0=Dom..6=Sáb, start_time "HH:mm", end_time "HH:mm", profile)`.
El agente evalúa con **su reloj local** qué ventana cubre el momento actual:

| Modo del perfil activo | Efecto |
|---|---|
| `local-filter` | DNS del equipo → `127.0.0.1`; bloquea la blacklist del perfil |
| `block-all` | DNS → `127.0.0.1`; responde NXDOMAIN a **todas** las consultas (sin internet) |
| `off` / sin ventana activa | Restaura el DNS original (por DHCP o estático, según corresponda) |

Dispositivo **sin** `schedule` → comportamiento heredado: aplica `dns.mode` +
`blacklist` global del payload.

## Estructura

```
src/ParentCLT.Agent/
  Program.cs                 # host del servicio + modo "restore-dns" (CLI)
  Services/
    PolicyWorker.cs          # ciclo registro -> política -> schedule -> heartbeat
    DnsFilterServer.cs       # mini servidor DNS en 127.0.0.1:53 (blacklist + DoH)
    DnsManager.cs            # WMI: aplicar/restaurar DNS (DHCP-aware)
    ConfigStore.cs           # state.json + DTOs del contrato API v1
Installer/
  installer.iss              # instalador Inno Setup (servicio + watchdog + restore al desinstalar)
  appsettings.template.json  # placeholders __SERVER_URL__ / __DEVICE_NAME__
  watchdog.bat               # reactiva el servicio si está caído
```

## Compilar y empaquetar

```powershell
# 1) Publicar el agente self-contained (incluye el runtime .NET 10)
dotnet publish src/ParentCLT.Agent/ParentCLT.Agent.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish

# 2) Generar el instalador (Inno Setup 6)
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" "Installer/installer.iss"
# -> Installer/Output/ParentCLT-Setup.exe (~27 MB, self-contained)
```

> Empaquetar SIEMPRE después de publicar: el instalador toma todo el contenido
> de `artifacts/publish`.

## Instalación

Interactiva: ejecutar `ParentCLT-Setup.exe` (pide URL del servidor y nombre del
dispositivo). Silenciosa:

```cmd
ParentCLT-Setup.exe /SERVERURL=http://192.168.1.7:8899 /DEVICENAME=PC-Sala /SILENT
```

Crea: servicio Windows `ParentCLT Agent` (delayed-auto + recovery), tarea
programada `ParentCLT Watchdog` (SYSTEM, cada 5 min) y genera `appsettings.json`.
Al **desinstalar**: detiene y mata el proceso, restaura el DNS (DHCP o estático),
elimina servicio, tarea y datos.

## Notas de seguridad

- Binarios **sin firma digital**: Defender puede marcarlos como PUA (típico en
  software parental sin firma) y Smart App Control (Win 11) bloquea el instalador.
  Para pruebas: exclusiones de Defender en `C:\Program Files\ParentCLT` y
  `%ProgramData%\ParentCLT`, y desactivar SAC. Para producción: firmar el código.
- El token del dispositivo viaja en `Authorization: Bearer`; usa HTTPS en producción.
