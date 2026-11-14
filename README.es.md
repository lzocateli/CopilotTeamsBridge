# CopilotTeamsBridge

[Português (Brasil)](README.md) | [English](README.en.md) | **Español**

Worker .NET 10 independiente que lleva las solicitudes de confirmación y de entrada de una sesión de GitHub Copilot (Agent Host/AHP) a un chat de Microsoft Teams. El transporte es el **webhook de un flujo de Teams Workflows (Power Automate)**, sin Microsoft Graph. La respuesta dada en Teams vuelve a la **misma sesión**.

Puede usarlo cualquier proyecto: el worker no conoce el repositorio de origen, solo el canal de chat del Agent Host y el webhook configurados.

> **Paso a paso completo:** [tutorial de configuración](docs/tutorial-configuracao.md) (en portugués) — Power Automate, host Linux, Agent Host y validación.

## Cómo funciona

1. El worker se conecta al Agent Host local (WebSocket en loopback), se suscribe al `ChatChannel` configurado y espera solicitudes (`inputRequested`, confirmaciones de herramientas).
2. Para cada solicitud, arma una Adaptive Card y hace un `POST` al webhook del Workflow. El payload incluye un `correlationToken` firmado con HMAC.
3. El Workflow publica la tarjeta en el chat, espera la respuesta y devuelve `202 Accepted` con `Location`. El worker consulta esa URL hasta recibir `{ responder, data }`.
4. El worker valida el token, la expiración y la identidad (`responder.objectId`/`tenantId`), y solo entonces envía la decisión al Agent Host. Nunca aprueba nada por sí solo.

Restricciones de seguridad:

- El Agent Host no se expone; el endpoint debe ser loopback.
- Sin redirecciones ni cookies, y el polling solo va a hosts aprobados.
- Los secretos se guardan en archivos con permiso `600`. Los logs solo muestran fingerprints.

## Requisitos

- .NET SDK 10 (ver `global.json`).
- GitHub Copilot Agent Host en ejecución en el mismo host, con token de conexión.
- Un flujo en Teams Workflows con:
  1. Trigger **"When a Teams webhook request is received"**.
  2. Acción **"Post adaptive card in a chat or channel and wait for a response"**, usando la tarjeta recibida en el cuerpo.
  3. Acción **Response** con *Asynchronous response* activado, que devuelva `{ "responder": <responder de la acción anterior>, "data": <datos enviados por la tarjeta> }`.

## Configuración

Toda la configuración está en la sección `TeamsBridge` de [appsettings.json](src/CopilotTeamsBridge.Worker/appsettings.json). Ese archivo está versionado y solo contiene placeholders.

Los valores reales van en `appsettings.Local.json`, junto al binario e incluido en `.gitignore`:

```json
{
  "TeamsBridge": {
    "DryRun": false,
    "CorrelationKeyFile": "/home/<usuario>/.config/copilot-teams-bridge/correlation.key",
    "AgentHost": {
      "Endpoint": "ws://127.0.0.1:<puerto-del-agent-host>/",
      "TokenFile": "/home/<usuario>/.config/copilot-teams-bridge/agent-host.token",
      "ChatChannel": "ahp-chat:/<id-del-chat>"
    },
    "Workflow": {
      "WebhookUrl": "https://<host-del-trigger>/<ruta-del-webhook>",
      "AuthMode": "bearer",
      "BearerTokenFile": "/home/<usuario>/.config/copilot-teams-bridge/workflow.bearer"
    },
    "Responder": {
      "AllowedObjectId": "<object-id-entra>",
      "AllowedTenantId": "<tenant-id-entra>"
    }
  }
}
```

La configuración se carga en este orden; cada fuente sobrescribe la anterior:

1. `appsettings.json`
2. `appsettings.Local.json`
3. variables `TEAMSBRIDGE_*` (por ejemplo, `TEAMSBRIDGE_TeamsBridge__Workflow__WebhookUrl`)
4. argumentos de línea de comandos (por ejemplo, `--TeamsBridge:AgentHost:ChatChannel=ahp-chat:/<id>`)

Reglas de los campos:

| Campo | Regla |
|-------|-------|
| `DryRun` | El valor predeterminado es `true`: arma las tarjetas, pero no llama al webhook ni responde al Agent Host. |
| `Workflow:WebhookUrl` | Obligatorio fuera de DryRun. Debe ser una URL HTTPS absoluta, sin credenciales ni fragmento. Si la URL lleva firma (`sig=`), trátela como secreto. |
| `Workflow:AllowedPollingHosts` | Hosts aceptados en el `Location` del polling. Si está vacía, solo se acepta el host de la `WebhookUrl`. |
| `Workflow:AuthMode` | `bearer` envía el contenido de `BearerTokenFile`. `url-secret` no envía `Authorization` y depende solo de la firma de la URL. |
| `CorrelationKeyFile` | Clave HMAC en base64, con al menos 32 bytes. |

Para preparar los secretos:

```bash
install -d -m 700 ~/.config/copilot-teams-bridge
openssl rand -base64 32 > ~/.config/copilot-teams-bridge/correlation.key
# guarde el token del Agent Host y el bearer del Workflow sin imprimirlos en la terminal
chmod 600 ~/.config/copilot-teams-bridge/*
```

## Uso

```bash
dotnet build CopilotTeamsBridge.slnx
dotnet test CopilotTeamsBridge.slnx
```

Para descubrir el `ChatChannel`, liste los chats del Agent Host. El comando solo lee y termina, sin enviar nada a Teams; el chat marcado con `*` es el principal de cada sesión (detalles en el [tutorial, pt-BR](docs/tutorial-configuracao.md#54-canal-do-chat)):

```bash
dotnet run --project src/CopilotTeamsBridge.Worker -- --list-chats \
  --TeamsBridge:AgentHost:Endpoint=ws://127.0.0.1:<puerto>/ \
  --TeamsBridge:AgentHost:TokenFile=<archivo-del-token>
```

```bash
dotnet run --project src/CopilotTeamsBridge.Worker -- --TeamsBridge:AgentHost:ChatChannel=ahp-chat:/<id-del-chat>
```

Cada instancia atiende **un** chat del Agent Host. Para atender varios proyectos o sesiones, ejecute una instancia por `ChatChannel`. Ejemplo de unidad systemd de usuario (`~/.config/systemd/user/copilot-teams-bridge@.service`):

```ini
[Unit]
Description=CopilotTeamsBridge (%i)

[Service]
ExecStart=%h/.dotnet/dotnet %h/apps/copilot-teams-bridge/copilot-teams-bridge.dll --TeamsBridge:AgentHost:ChatChannel=ahp-chat:/%i
Restart=on-failure
UMask=0077

[Install]
WantedBy=default.target
```

## Contenedor (Podman rootless)

El [`Containerfile`](Containerfile) genera una imagen sin secretos que se ejecuta como usuario sin privilegios. Los secretos y la configuración se montan en solo lectura en `/secrets` y `/app/appsettings.Local.json`. Ejemplo:

```bash
podman build -t localhost/copilot-teams-bridge:latest -f Containerfile .
podman run --rm --network=pasta:-T,<puerto> --userns=keep-id:uid=1654,gid=1654 \
  --read-only --cap-drop=all --security-opt=no-new-privileges \
  -v ~/.config/copilot-teams-bridge:/secrets:ro,Z \
  -v ~/.config/copilot-teams-bridge/appsettings.Local.json:/app/appsettings.Local.json:ro,Z \
  localhost/copilot-teams-bridge:latest --list-chats
```

Sin `--list-chats`, el mismo comando ejecuta el worker. El servicio con Podman Quadlet y los detalles de red y UID están en la [sección 10 del tutorial (pt-BR)](docs/tutorial-configuracao.md#10-rodar-em-container-podman-rootless).

## Limitaciones aún no comprobadas

- Publicación en el chat con uno mismo (`48:notes`) mediante el Workflow.
- *Asynchronous response* en el trigger de webhook de Teams (la documentación cubre el trigger HTTP).
- Si el Agent Host entrega `inputRequested` a un segundo cliente y acepta su respuesta.
- Otras versiones del Agent Host: la conexión (`ws://127.0.0.1:<puerto>/` con token), la suscripción al chat y `--list-chats` se validaron solo en la versión usada en el desarrollo.

Mantenga `DryRun=true` hasta validar estos puntos en su entorno.

## Licencia

Distribuido bajo la licencia MIT. Consulte [LICENSE](LICENSE).
