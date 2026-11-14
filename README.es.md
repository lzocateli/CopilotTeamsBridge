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

- Host Linux con Podman 4.4 o posterior en modo rootless. El .NET SDK solo es necesario para desarrollar (vea [Desarrollo](#desarrollo)).
- Agent Host de Copilot en ejecución en el mismo host, con puerto y token de conexión fijos.
- Un flujo en Teams Workflows con:
  1. Trigger **"When a Teams webhook request is received"**.
  2. Acción **"Post adaptive card in a chat or channel and wait for a response"**, usando la tarjeta recibida en el cuerpo.
  3. Acción **Response** con *Asynchronous response* activado, que devuelva `{ "responder": <responder de la acción anterior>, "data": <datos enviados por la tarjeta> }`.

## Instalación

El worker se ejecuta en un contenedor. La imagen, generada por el [`Containerfile`](Containerfile), no guarda secretos y se ejecuta como el usuario sin privilegios `app` (UID 1654).

```bash
git clone https://github.com/lzocateli/CopilotTeamsBridge.git && cd CopilotTeamsBridge
podman build -t localhost/copilot-teams-bridge:latest -f Containerfile .

install -d -m 700 ~/.config/copilot-teams-bridge
openssl rand -base64 32 > ~/.config/copilot-teams-bridge/correlation.key
# escriba el token del Agent Host en agent-host.token (y, en modo bearer, el token en workflow.bearer) sin mostrarlos en la terminal
chmod 600 ~/.config/copilot-teams-bridge/*
```

El directorio `~/.config/copilot-teams-bridge` se monta en solo lectura en `/secrets`. La imagen ya apunta `CorrelationKeyFile` a `/secrets/correlation.key` y `AgentHost:TokenFile` a `/secrets/agent-host.token`.

## Configuración

Los valores predeterminados están en la sección `TeamsBridge` de [appsettings.json](src/CopilotTeamsBridge.Worker/appsettings.json), incluido en la imagen y solo con placeholders. Los valores reales van en `~/.config/copilot-teams-bridge/appsettings.Local.json` (permiso `600`), montado en `/app/appsettings.Local.json`:

```json
{
  "TeamsBridge": {
    "DryRun": false,
    "AgentHost": {
      "Endpoint": "ws://127.0.0.1:<puerto-del-agent-host>/",
      "ChatChannel": "ahp-chat:/<id-del-chat>"
    },
    "Workflow": {
      "WebhookUrl": "https://<host-del-trigger>/<ruta-del-webhook>",
      "AuthMode": "bearer",
      "BearerTokenFile": "/secrets/workflow.bearer"
    },
    "Responder": {
      "AllowedObjectId": "<object-id-entra>",
      "AllowedTenantId": "<tenant-id-entra>"
    }
  }
}
```

Las rutas de archivo de la configuración se ven desde dentro del contenedor: use siempre el prefijo `/secrets/`.

La configuración se carga en este orden; cada fuente sobrescribe la anterior:

1. `appsettings.json` de la imagen
2. `appsettings.Local.json` montado
3. variables `TEAMSBRIDGE_*` (la imagen define `TEAMSBRIDGE_TeamsBridge__CorrelationKeyFile` y `TEAMSBRIDGE_TeamsBridge__AgentHost__TokenFile`)
4. argumentos después del nombre de la imagen en `podman run` (por ejemplo, `--TeamsBridge:AgentHost:ChatChannel=ahp-chat:/<id>`)

Reglas de los campos:

| Campo | Regla |
|-------|-------|
| `DryRun` | El valor predeterminado es `true`: arma las tarjetas, pero no llama al webhook ni responde al Agent Host. |
| `AgentHost:Endpoint` | Debe ser loopback (`127.0.0.1` o `localhost`). En el contenedor, la red `pasta:-T,<puerto>` reenvía ese puerto al Agent Host del host. |
| `Workflow:WebhookUrl` | Obligatorio fuera de DryRun. Debe ser una URL HTTPS absoluta, sin credenciales ni fragmento. Si la URL lleva firma (`sig=`), trátela como secreto. |
| `Workflow:AllowedPollingHosts` | Hosts aceptados en el `Location` del polling. Si está vacía, solo se acepta el host de la `WebhookUrl`. |
| `Workflow:AuthMode` | `bearer` envía el contenido de `BearerTokenFile`. `url-secret` no envía `Authorization` y depende solo de la firma de la URL. |
| `CorrelationKeyFile` | Clave HMAC en base64, con al menos 32 bytes. |

## Uso

Todas las ejecuciones usan las mismas opciones de aislamiento: red limitada al puerto del Agent Host, UID del host mapeado al usuario `app` (necesario para leer los archivos `600`), sistema de archivos de solo lectura y ninguna capability.

```bash
podman run --rm --network=pasta:-T,<puerto> --userns=keep-id:uid=1654,gid=1654 \
  --read-only --cap-drop=all --security-opt=no-new-privileges \
  -v ~/.config/copilot-teams-bridge:/secrets:ro,z \
  -v ~/.config/copilot-teams-bridge/appsettings.Local.json:/app/appsettings.Local.json:ro,z \
  localhost/copilot-teams-bridge:latest --list-chats
```

- `--list-chats` lista los chats del Agent Host y termina sin enviar nada a Teams. El chat marcado con `*` es el principal de cada sesión; cópielo en `AgentHost:ChatChannel` (detalles en la [sección 6.3 del tutorial, pt-BR](docs/tutorial-configuracao.md#63-descobrir-o-canal-do-chat)).
- Sin `--list-chats`, el mismo comando ejecuta el worker en primer plano.
- Para ejecutarlo como servicio, use un archivo Quadlet en `~/.config/containers/systemd/` ([sección 8 del tutorial, pt-BR](docs/tutorial-configuracao.md#8-rodar-como-serviço-com-quadlet)).

Cada instancia atiende **un** chat del Agent Host. Para atender varios proyectos o sesiones, ejecute una instancia por `ChatChannel`.

## Desarrollo

Para modificar el código, use el .NET 10 SDK (vea `global.json`):

```bash
dotnet build CopilotTeamsBridge.slnx
dotnet test CopilotTeamsBridge.slnx
```

## Limitaciones aún no comprobadas

- Publicación en el chat con uno mismo (`48:notes`) mediante el Workflow.
- *Asynchronous response* en el trigger de webhook de Teams (la documentación cubre el trigger HTTP).
- Si el Agent Host entrega `inputRequested` a un segundo cliente y acepta su respuesta.
- Otras versiones del Agent Host: la conexión (`ws://127.0.0.1:<puerto>/` con token), la suscripción al chat y `--list-chats` se validaron solo en la versión usada en el desarrollo.

Mantenga `DryRun=true` hasta validar estos puntos en su entorno.

## Licencia

Distribuido bajo la licencia MIT. Consulte [LICENSE](LICENSE).
