# CopilotTeamsBridge

[Português (Brasil)](README.md) | **English** | [Español](README.es.md)

Standalone .NET 10 worker that forwards confirmation and input requests from a GitHub Copilot session (Agent Host/AHP) to a Microsoft Teams chat. Transport is a **Teams Workflows (Power Automate) webhook** — no Microsoft Graph required. The answer given in Teams goes back to the **same session**.

It can be used by any project: the worker knows nothing about the source repository, only the configured Agent Host chat channel and webhook.

> **Full walkthrough:** [setup tutorial](docs/tutorial-configuracao.md) (in Portuguese) — Power Automate, Linux host, Agent Host and validation.

## How it works

1. The worker connects to the local Agent Host (loopback WebSocket), subscribes to the configured `ChatChannel` and waits for requests (`inputRequested`, tool confirmations).
2. For each request it builds an Adaptive Card and sends a `POST` to the Workflow webhook. The payload carries an HMAC-signed `correlationToken`.
3. The Workflow posts the card to the chat, waits for the answer and returns `202 Accepted` with a `Location` header. The worker polls that URL until it receives `{ responder, data }`.
4. The worker validates the token, the expiration and the identity (`responder.objectId`/`tenantId`), and only then forwards the decision to the Agent Host. It never approves anything on its own.

Security constraints:

- The Agent Host is never exposed; the endpoint must be loopback.
- No redirects or cookies, and polling only goes to approved hosts.
- Secrets live in files with `600` permissions. Logs only show fingerprints.

## Requirements

- Linux host with Podman 4.4 or later in rootless mode. The .NET SDK is only needed for development (see [Development](#development)).
- Copilot Agent Host running on the same host, with a fixed port and connection token.
- A Teams Workflows flow with:
  1. Trigger **"When a Teams webhook request is received"**.
  2. Action **"Post adaptive card in a chat or channel and wait for a response"**, using the card received in the body.
  3. **Response** action with *Asynchronous response* turned on, returning `{ "responder": <responder from the previous action>, "data": <data submitted by the card> }`.

## Installation

The worker runs in a container. The image, built from the [`Containerfile`](Containerfile), holds no secrets and runs as the unprivileged user `app` (UID 1654).

```bash
git clone https://github.com/lzocateli/CopilotTeamsBridge.git && cd CopilotTeamsBridge
podman build -t localhost/copilot-teams-bridge:latest -f Containerfile .

install -d -m 700 ~/.config/copilot-teams-bridge
openssl rand -base64 32 > ~/.config/copilot-teams-bridge/correlation.key
# write the Agent Host token to agent-host.token (and, in bearer mode, the token to workflow.bearer) without printing them to the terminal
chmod 600 ~/.config/copilot-teams-bridge/*
```

The `~/.config/copilot-teams-bridge` directory is mounted read-only at `/secrets`. The image already points `CorrelationKeyFile` to `/secrets/correlation.key` and `AgentHost:TokenFile` to `/secrets/agent-host.token`.

## Configuration

Defaults live in the `TeamsBridge` section of [appsettings.json](src/CopilotTeamsBridge.Worker/appsettings.json), baked into the image with placeholders only. Real values go in `~/.config/copilot-teams-bridge/appsettings.Local.json` (mode `600`), mounted at `/app/appsettings.Local.json`:

```json
{
  "TeamsBridge": {
    "DryRun": false,
    "AgentHost": {
      "Endpoint": "ws://127.0.0.1:<agent-host-port>/",
      "ChatChannel": "ahp-chat:/<chat-id>"
    },
    "Workflow": {
      "WebhookUrl": "https://<trigger-host>/<webhook-path>",
      "AuthMode": "bearer",
      "BearerTokenFile": "/secrets/workflow.bearer"
    },
    "Responder": {
      "AllowedObjectId": "<entra-object-id>",
      "AllowedTenantId": "<entra-tenant-id>"
    }
  }
}
```

File paths in the configuration are seen from inside the container: always use the `/secrets/` prefix.

Configuration is loaded in this order; each source overrides the previous one:

1. `appsettings.json` from the image
2. the mounted `appsettings.Local.json`
3. `TEAMSBRIDGE_*` environment variables (the image sets `TEAMSBRIDGE_TeamsBridge__CorrelationKeyFile` and `TEAMSBRIDGE_TeamsBridge__AgentHost__TokenFile`)
4. arguments after the image name in `podman run` (for example, `--TeamsBridge:AgentHost:ChatChannel=ahp-chat:/<id>`)

Field rules:

| Field | Rule |
|-------|------|
| `DryRun` | Defaults to `true`: builds the cards but neither calls the webhook nor answers the Agent Host. |
| `AgentHost:Endpoint` | Must be loopback (`127.0.0.1` or `localhost`). In the container, the `pasta:-T,<port>` network forwards that port to the host's Agent Host. |
| `Workflow:WebhookUrl` | Required outside DryRun. Must be an absolute HTTPS URL, without credentials or fragment. If the URL carries a signature (`sig=`), treat it as a secret. |
| `Workflow:AllowedPollingHosts` | Hosts accepted in the polling `Location`. When empty, only the `WebhookUrl` host is accepted. |
| `Workflow:AuthMode` | `bearer` sends the contents of `BearerTokenFile`. `url-secret` sends no `Authorization` header and relies on the URL signature alone. |
| `CorrelationKeyFile` | Base64 HMAC key with at least 32 bytes. |

## Usage

Every run uses the same isolation options: network limited to the Agent Host port, host UID mapped to the `app` user (required to read the `600` files), read-only file system and no capabilities.

```bash
podman run --rm --network=pasta:-T,<port> --userns=keep-id:uid=1654,gid=1654 \
  --read-only --cap-drop=all --security-opt=no-new-privileges \
  -v ~/.config/copilot-teams-bridge:/secrets:ro,z \
  -v ~/.config/copilot-teams-bridge/appsettings.Local.json:/app/appsettings.Local.json:ro,z \
  localhost/copilot-teams-bridge:latest --list-chats
```

- `--list-chats` lists the Agent Host chats and exits without sending anything to Teams. The chat marked with `*` is the main chat of each session; copy it to `AgentHost:ChatChannel` (details in [section 6.3 of the tutorial, pt-BR](docs/tutorial-configuracao.md#63-descobrir-o-canal-do-chat)).
- Without `--list-chats`, the same command runs the worker in the foreground.
- To run it as a service, use a Quadlet file in `~/.config/containers/systemd/` ([section 8 of the tutorial, pt-BR](docs/tutorial-configuracao.md#8-rodar-como-serviço-com-quadlet)).

Each instance serves **one** Agent Host chat. To serve several projects or sessions, run one instance per `ChatChannel`.

## Development

To change the code, use the .NET 10 SDK (see `global.json`):

```bash
dotnet build CopilotTeamsBridge.slnx
dotnet test CopilotTeamsBridge.slnx
```

## Not yet verified

- Posting to the self-chat (`48:notes`) through the Workflow.
- *Asynchronous response* on the Teams webhook trigger (the documentation covers the HTTP trigger).
- Whether the Agent Host delivers `inputRequested` to a second client and accepts its answer.
- Other Agent Host versions: the connection (`ws://127.0.0.1:<port>/` with token), the chat subscription and `--list-chats` were verified only on the version used during development.

Keep `DryRun=true` until you have validated these points in your environment.

## License

Distributed under the MIT License. See [LICENSE](LICENSE).
