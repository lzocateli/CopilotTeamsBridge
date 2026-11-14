# CopilotTeamsBridge

**Português (Brasil)** | [English](README.en.md) | [Español](README.es.md)

Worker .NET 10 independente que leva pedidos de confirmação e de entrada de uma sessão do Copilot (Agent Host/AHP) para um chat do Microsoft Teams. O transporte é o **webhook de um fluxo do Teams Workflows**, sem Microsoft Graph. A resposta dada no Teams volta para a **mesma sessão**.

Pode ser usado por qualquer projeto: o worker não conhece o repositório de origem, só o canal de chat do Agent Host e o webhook configurados.

## Como funciona

1. O worker se conecta ao Agent Host local (WebSocket em loopback), assina o `ChatChannel` configurado e aguarda pedidos (`inputRequested`, confirmações de ferramenta).
2. Para cada pedido, monta um Adaptive Card e faz `POST` no webhook do Workflow. O payload leva um `correlationToken` assinado por HMAC.
3. O Workflow publica o cartão no chat, espera a resposta e devolve `202 Accepted` com `Location`. O worker consulta essa URL até receber `{ responder, data }`.
4. O worker valida o token, a expiração e a identidade (`responder.objectId`/`tenantId`) e só então encaminha a decisão ao Agent Host. Ele nunca aprova nada sozinho.

Restrições de segurança:

- O Agent Host não é exposto; o endpoint precisa ser loopback.
- Não há redirects nem cookies, e o polling só vai para hosts aprovados.
- Segredos ficam em arquivos com permissão `600`. Os logs mostram só fingerprints.

## Requisitos

- .NET SDK 10 (veja `global.json`).
- Agent Host do Copilot em execução no mesmo host, com token de conexão.
- Um fluxo no Teams Workflows com:
  1. Trigger **"When a Teams webhook request is received"**.
  2. Ação **"Post adaptive card in a chat or channel and wait for a response"**, usando o cartão recebido no corpo.
  3. Ação **Response** com *Asynchronous response* ligado, devolvendo `{ "responder": <responder da ação anterior>, "data": <dados enviados pelo cartão> }`.

## Configuração

Toda a configuração fica na seção `TeamsBridge` de [appsettings.json](src/CopilotTeamsBridge.Worker/appsettings.json). Esse arquivo é versionado e só contém placeholders.

Os valores reais vão em `appsettings.Local.json`, que fica ao lado do binário e está no `.gitignore`:

```json
{
  "TeamsBridge": {
    "DryRun": false,
    "CorrelationKeyFile": "/home/<usuario>/.config/copilot-teams-bridge/correlation.key",
    "AgentHost": {
      "Endpoint": "ws://127.0.0.1:<porta-do-agent-host>/",
      "TokenFile": "/home/<usuario>/.config/copilot-teams-bridge/agent-host.token",
      "ChatChannel": "ahp-chat:/<id-do-chat>"
    },
    "Workflow": {
      "WebhookUrl": "https://<host-do-trigger>/<caminho-do-webhook>",
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

A configuração é carregada nesta ordem; cada fonte sobrescreve a anterior:

1. `appsettings.json`
2. `appsettings.Local.json`
3. variáveis `TEAMSBRIDGE_*` (por exemplo, `TEAMSBRIDGE_TeamsBridge__Workflow__WebhookUrl`)
4. argumentos de linha de comando (por exemplo, `--TeamsBridge:AgentHost:ChatChannel=ahp-chat:/<id>`)

Regras dos campos:

| Campo | Regra |
|-------|-------|
| `DryRun` | O padrão é `true`: monta os cartões, mas não chama o webhook nem responde ao Agent Host. |
| `Workflow:WebhookUrl` | Obrigatório fora do DryRun. Precisa ser HTTPS absoluta, sem credenciais nem fragmento. Se a URL tiver assinatura (`sig=`), trate-a como segredo. |
| `Workflow:AllowedPollingHosts` | Lista os hosts aceitos no `Location` do polling. Se ficar vazia, só o host da `WebhookUrl` é aceito. |
| `Workflow:AuthMode` | `bearer` envia o conteúdo de `BearerTokenFile`. `url-secret` não envia `Authorization` e conta só com a assinatura da URL. |
| `CorrelationKeyFile` | Chave HMAC em base64, com pelo menos 32 bytes. |

Para preparar os segredos:

```bash
install -d -m 700 ~/.config/copilot-teams-bridge
openssl rand -base64 32 > ~/.config/copilot-teams-bridge/correlation.key
# grave o token do Agent Host e o bearer do Workflow sem imprimi-los no terminal
chmod 600 ~/.config/copilot-teams-bridge/*
```

## Uso

```bash
dotnet build CopilotTeamsBridge.slnx
dotnet test CopilotTeamsBridge.slnx
dotnet run --project src/CopilotTeamsBridge.Worker -- --TeamsBridge:AgentHost:ChatChannel=ahp-chat:/<id-do-chat>
```

Cada instância atende **um** chat do Agent Host. Para atender vários projetos ou sessões, rode uma instância por `ChatChannel`. Exemplo de unit systemd de usuário (`~/.config/systemd/user/copilot-teams-bridge@.service`):

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

## Limitações ainda não comprovadas

- Publicação no chat consigo mesmo (`48:notes`) pelo Workflow.
- *Asynchronous response* no trigger de webhook do Teams (a documentação cobre o trigger HTTP).
- Se o Agent Host entrega `inputRequested` a um segundo cliente e aceita a resposta dele.
- A URI exata do WebSocket do Agent Host em cada versão.

Mantenha `DryRun=true` até validar esses pontos no seu ambiente.

## Licença

Distribuído sob a licença MIT. Veja [LICENSE](LICENSE).
