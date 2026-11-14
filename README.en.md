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

- .NET SDK 10 (see `global.json`).
- GitHub Copilot Agent Host running on the same host, with a connection token.
- A Teams Workflows flow with:
  1. Trigger **"When a Teams webhook request is received"**.
  2. Action **"Post adaptive card in a chat or channel and wait for a response"**, using the card received in the body.
  3. **Response** action with *Asynchronous response* enabled, returning `{ "responder": <responder from the previous action>, "data": <data submitted by the card> }`.

## Configuration

All settings live in the `TeamsBridge` section of [appsettings.json](src/CopilotTeamsBridge.Worker/appsettings.json). That file is versioned and contains placeholders only.

Real values go in `appsettings.Local.json`, placed next to the binary and listed in `.gitignore`:

```json
{
  "TeamsBridge": {
    "DryRun": false,
    "CorrelationKeyFile": "/home/<user>/.config/copilot-teams-bridge/correlation.key",
    "AgentHost": {
      "Endpoint": "ws://127.0.0.1:<agent-host-port>/",
      "TokenFile": "/home/<user>/.config/copilot-teams-bridge/agent-host.token",
      "ChatChannel": "ahp-chat:/<chat-id>"
    },
    "Workflow": {
      "WebhookUrl": "https://<trigger-host>/<webhook-path>",
      "AuthMode": "bearer",
      "BearerTokenFile": "/home/<user>/.config/copilot-teams-bridge/workflow.bearer"
    },
    "Responder": {
      "AllowedObjectId": "<entra-object-id>",
      "AllowedTenantId": "<entra-tenant-id>"
    }
  }
}
```

Configuration is loaded in this order; each source overrides the previous one:

1. `appsettings.json`
2. `appsettings.Local.json`
3. `TEAMSBRIDGE_*` environment variables (for example, `TEAMSBRIDGE_TeamsBridge__Workflow__WebhookUrl`)
4. command-line arguments (for example, `--TeamsBridge:AgentHost:ChatChannel=ahp-chat:/<id>`)

Field rules:

| Field | Rule |
|-------|------|
| `DryRun` | Defaults to `true`: builds the cards but neither calls the webhook nor answers the Agent Host. |
| `Workflow:WebhookUrl` | Required outside DryRun. Must be an absolute HTTPS URL, without credentials or fragment. If the URL carries a signature (`sig=`), treat it as a secret. |
| `Workflow:AllowedPollingHosts` | Hosts accepted in the polling `Location`. When empty, only the `WebhookUrl` host is accepted. |
| `Workflow:AuthMode` | `bearer` sends the contents of `BearerTokenFile`. `url-secret` sends no `Authorization` header and relies on the URL signature alone. |
| `CorrelationKeyFile` | Base64 HMAC key with at least 32 bytes. |

To prepare the secrets:

```bash
install -d -m 700 ~/.config/copilot-teams-bridge
openssl rand -base64 32 > ~/.config/copilot-teams-bridge/correlation.key
# write the Agent Host token and the Workflow bearer without printing them to the terminal
chmod 600 ~/.config/copilot-teams-bridge/*
```

## Usage

```bash
dotnet build CopilotTeamsBridge.slnx
dotnet test CopilotTeamsBridge.slnx
dotnet run --project src/CopilotTeamsBridge.Worker -- --TeamsBridge:AgentHost:ChatChannel=ahp-chat:/<chat-id>
```

Each instance serves **one** Agent Host chat. To serve several projects or sessions, run one instance per `ChatChannel`. Example systemd user unit (`~/.config/systemd/user/copilot-teams-bridge@.service`):

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

## Not yet verified

- Posting to the self-chat (`48:notes`) through the Workflow.
- *Asynchronous response* on the Teams webhook trigger (the documentation covers the HTTP trigger).
- Whether the Agent Host delivers `inputRequested` to a second client and accepts its answer.
- The exact Agent Host WebSocket URI in each version.

Keep `DryRun=true` until you have validated these points in your environment.

## License

Distributed under the MIT License. See [LICENSE](LICENSE).
