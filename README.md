# CopilotTeamsBridge

**Português (Brasil)** | [English](README.en.md) | [Español](README.es.md)

Worker .NET 10 independente que leva pedidos de confirmação e de entrada de uma sessão do Copilot (Agent Host/AHP) para um chat do Microsoft Teams. O transporte é o **webhook de um fluxo do Teams Workflows**, sem Microsoft Graph. A resposta dada no Teams volta para a **mesma sessão**.

Pode ser usado por qualquer projeto: o worker não conhece o repositório de origem, só o canal de chat do Agent Host e o webhook configurados.

> **Passo a passo completo:** [tutorial de configuração](docs/tutorial-configuracao.md) — Power Automate, host Linux, Agent Host e validação.

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

- Host Linux com Podman 4.4 ou mais recente em modo rootless. O .NET SDK só é necessário para desenvolver (veja [Desenvolvimento](#desenvolvimento)).
- Agent Host do Copilot em execução no mesmo host, com porta e token de conexão fixos.
- Um fluxo no Teams Workflows com:
  1. Trigger **"When a Teams webhook request is received"**.
  2. Ação **"Post adaptive card in a chat or channel and wait for a response"**, usando o cartão recebido no corpo.
  3. Ação **Response** com *Asynchronous response* ligado, devolvendo `{ "responder": <responder da ação anterior>, "data": <dados enviados pelo cartão> }`.

## Instalação

O worker roda em container. A imagem, gerada pelo [`Containerfile`](Containerfile), não guarda segredos e roda como o usuário sem privilégios `app` (UID 1654).

```bash
git clone https://github.com/lzocateli/CopilotTeamsBridge.git && cd CopilotTeamsBridge
podman build -t localhost/copilot-teams-bridge:latest -f Containerfile .

install -d -m 700 ~/.config/copilot-teams-bridge
openssl rand -base64 32 > ~/.config/copilot-teams-bridge/correlation.key
# grave o token do Agent Host em agent-host.token (e, no modo bearer, o token em workflow.bearer) sem exibi-los no terminal
chmod 600 ~/.config/copilot-teams-bridge/*
```

O diretório `~/.config/copilot-teams-bridge` é montado somente leitura em `/secrets`. A imagem já aponta `CorrelationKeyFile` para `/secrets/correlation.key` e `AgentHost:TokenFile` para `/secrets/agent-host.token`.

## Configuração

Os valores padrão ficam na seção `TeamsBridge` de [appsettings.json](src/CopilotTeamsBridge.Worker/appsettings.json), embutido na imagem e só com placeholders. Os valores reais vão em `~/.config/copilot-teams-bridge/appsettings.Local.json` (permissão `600`), montado em `/app/appsettings.Local.json`:

```json
{
  "TeamsBridge": {
    "DryRun": false,
    "AgentHost": {
      "Endpoint": "ws://127.0.0.1:<porta-do-agent-host>/",
      "ChatChannel": "ahp-chat:/<id-do-chat>"
    },
    "Workflow": {
      "WebhookUrl": "https://<host-do-trigger>/<caminho-do-webhook>",
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

Caminhos de arquivo na configuração são vistos de dentro do container: use sempre o prefixo `/secrets/`.

A configuração é carregada nesta ordem; cada fonte sobrescreve a anterior:

1. `appsettings.json` da imagem
2. `appsettings.Local.json` montado
3. variáveis `TEAMSBRIDGE_*` (a imagem define `TEAMSBRIDGE_TeamsBridge__CorrelationKeyFile` e `TEAMSBRIDGE_TeamsBridge__AgentHost__TokenFile`)
4. argumentos depois do nome da imagem no `podman run` (por exemplo, `--TeamsBridge:AgentHost:ChatChannel=ahp-chat:/<id>`)

Regras dos campos:

| Campo | Regra |
|-------|-------|
| `DryRun` | O padrão é `true`: monta os cartões, mas não chama o webhook nem responde ao Agent Host. |
| `AgentHost:Endpoint` | Precisa ser loopback (`127.0.0.1` ou `localhost`). No container, a rede `pasta:-T,<porta>` encaminha essa porta para o Agent Host do host. |
| `Workflow:WebhookUrl` | Obrigatório fora do DryRun. Precisa ser HTTPS absoluta, sem credenciais nem fragmento. Se a URL tiver assinatura (`sig=`), trate-a como segredo. |
| `Workflow:AllowedPollingHosts` | Lista os hosts aceitos no `Location` do polling. Se ficar vazia, só o host da `WebhookUrl` é aceito. |
| `Workflow:AuthMode` | `bearer` envia o conteúdo de `BearerTokenFile`. `url-secret` não envia `Authorization` e conta só com a assinatura da URL. |
| `CorrelationKeyFile` | Chave HMAC em base64, com pelo menos 32 bytes. |

## Uso

Todas as execuções usam as mesmas opções de isolamento: rede limitada à porta do Agent Host, UID do host mapeado para o usuário `app` (necessário para ler os arquivos `600`), sistema de arquivos somente leitura e nenhuma capability.

```bash
podman run --rm --network=pasta:-T,<porta> --userns=keep-id:uid=1654,gid=1654 \
  --read-only --cap-drop=all --security-opt=no-new-privileges \
  -v ~/.config/copilot-teams-bridge:/secrets:ro,z \
  -v ~/.config/copilot-teams-bridge/appsettings.Local.json:/app/appsettings.Local.json:ro,z \
  localhost/copilot-teams-bridge:latest --list-chats
```

- `--list-chats` lista os chats do Agent Host e termina sem enviar nada ao Teams. O chat marcado com `*` é o principal de cada sessão; copie-o para `AgentHost:ChatChannel` (detalhes na [seção 6.3 do tutorial](docs/tutorial-configuracao.md#63-descobrir-o-canal-do-chat)).
- Sem `--list-chats`, o mesmo comando roda o worker em primeiro plano.
- Para rodar como serviço, use um arquivo Quadlet em `~/.config/containers/systemd/` ([seção 8 do tutorial](docs/tutorial-configuracao.md#8-rodar-como-serviço-com-quadlet)).

Cada instância atende **um** chat do Agent Host. Para atender vários projetos ou sessões, rode uma instância por `ChatChannel`.

## Desenvolvimento

Para alterar o código, use o .NET SDK 10 (veja `global.json`):

```bash
dotnet build CopilotTeamsBridge.slnx
dotnet test CopilotTeamsBridge.slnx
```

## Limitações ainda não comprovadas

- Publicação no chat consigo mesmo (`48:notes`) pelo Workflow.
- *Asynchronous response* no trigger de webhook do Teams (a documentação cobre o trigger HTTP).
- Se o Agent Host entrega `inputRequested` a um segundo cliente e aceita a resposta dele.
- Outras versões do Agent Host: a conexão (`ws://127.0.0.1:<porta>/` com token), a inscrição no chat e o `--list-chats` foram validados só na versão usada no desenvolvimento.

Mantenha `DryRun=true` até validar esses pontos no seu ambiente.

## Licença

Distribuído sob a licença MIT. Veja [LICENSE](LICENSE).
