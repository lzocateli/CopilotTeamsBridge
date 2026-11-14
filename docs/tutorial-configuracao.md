# Tutorial: configurar o CopilotTeamsBridge de ponta a ponta

Este tutorial monta tudo o que o worker precisa para levar pedidos de uma sessão do GitHub Copilot (Agent Host) até o Microsoft Teams e trazer a resposta de volta para a **mesma sessão**:

1. [Pré-requisitos](#1-pré-requisitos)
2. [Criar o Workflow no Power Automate](#2-criar-o-workflow-no-power-automate)
3. [Testar o Workflow sozinho e descobrir seus IDs](#3-testar-o-workflow-sozinho-e-descobrir-seus-ids)
4. [Preparar o host Linux](#4-preparar-o-host-linux)
5. [Conectar ao Agent Host](#5-conectar-ao-agent-host)
6. [Configurar o worker](#6-configurar-o-worker)
7. [Validar em três etapas](#7-validar-em-três-etapas)
8. [Rodar como serviço](#8-rodar-como-serviço)
9. [Solução de problemas](#9-solução-de-problemas)

> **Antes de começar:** alguns pontos ainda não foram comprovados em ambiente real (veja [Limitações](../README.md#limitações-ainda-não-comprovadas)). Por isso o worker vem com `DryRun=true` e a validação da [seção 7](#7-validar-em-três-etapas) é feita em etapas.

Convenções usadas aqui:

- `<usuario>`, `<porta>` etc. são placeholders. Troque pelos seus valores.
- Comandos com `$` rodam no host Linux onde está o Agent Host.
- Os nomes de menus estão em inglês, como aparecem no Power Automate. Se a sua interface estiver em português, os nomes equivalentes aparecem entre parênteses.

---

## 1. Pré-requisitos

| Item | Como conferir |
|------|---------------|
| Conta Microsoft 365 corporativa com Teams e Power Automate | Acesse <https://make.powerautomate.com> e veja se o portal abre sem pedir licença. |
| App **Workflows** instalado no Teams | No Teams: **Apps** (barra lateral) → pesquise **Workflows** → **Add** (Adicionar) ou **Open** (Abrir). Sem ele, o envio de cartões falha. |
| Host Linux com o GitHub Copilot Agent Host rodando | `$ code agent host --help` responde e a sessão abre no VS Code. |
| .NET SDK 10 no host | `$ dotnet --list-sdks` mostra uma versão `10.x`. |
| `git`, `curl`, `openssl` e `python3` no host | `$ git --version && curl --version && openssl version && python3 --version` |

Se a sua empresa restringe conectores do Power Automate (políticas de DLP), confirme com o administrador que **Microsoft Teams** e **Request/Response** podem ser usados no mesmo fluxo.

---

## 2. Criar o Workflow no Power Automate

O Workflow faz três coisas: recebe o cartão do worker, publica no Teams e espera sua resposta, e devolve a resposta ao worker de forma assíncrona.

### 2.1 Abrir o portal e criar o fluxo

1. Abra <https://make.powerautomate.com> e entre com a conta corporativa.
2. No canto superior direito, em **Environment** (Ambiente), deixe o ambiente padrão da organização, normalmente com o nome do tenant e "(default)".
3. Na barra lateral esquerda, clique em **My flows** (Meus fluxos).
4. Clique em **+ New flow** (Novo fluxo) → **Instant cloud flow** (Fluxo da nuvem instantâneo).
5. Em **Flow name**, digite `CopilotTeamsBridge`.
6. Na lista de gatilhos, procure `Teams webhook` e selecione **When a Teams webhook request is received** (Quando uma solicitação de webhook do Teams é recebida).
7. Clique em **Create** (Criar). O designer abre com o gatilho no topo.

> **Alternativa pelo Teams:** **Apps** → **Workflows** → **Create** (Criar) → **Create from blank** (Criar do zero) abre o mesmo designer. Os passos seguintes são iguais. Prefira o portal: as configurações avançadas (Settings) ficam mais fáceis de achar lá.

### 2.2 Configurar o gatilho

1. Clique no cartão do gatilho **When a Teams webhook request is received**.
2. Em **Who can trigger the flow?** (Quem pode disparar o fluxo?), escolha uma das opções:

| Opção | Configuração no worker | Quando usar |
|-------|------------------------|-------------|
| **Anyone** (Qualquer pessoa) | `Workflow:AuthMode = "url-secret"` | **Recomendado para a primeira prova.** Quem tiver a URL consegue disparar o fluxo, porque a assinatura `sig=` dentro dela funciona como senha. Guarde a URL como segredo. |
| **Specific users in my tenant** (Usuários específicos no meu locatário) | `Workflow:AuthMode = "bearer"` + `BearerTokenFile` | Mais restrito, mas exige um token Entra com audiência `https://service.flow.microsoft.com/`, que expira em cerca de 1 hora e precisa ser renovado por fora do worker. **Ainda não validado com este worker.** |

### 2.3 Publicar o cartão e esperar a resposta

1. Abaixo do gatilho, clique em **+** → **Add an action** (Adicionar uma ação).
2. Pesquise `Microsoft Teams` e selecione **Post adaptive card in a chat or channel and wait for a response** (Postar cartão adaptável em um chat ou canal e aguardar uma resposta).
3. Se aparecer um pedido de conexão, entre com a mesma conta corporativa.
4. Renomeie a ação para **`AguardarResposta`**: clique nos **...** (reticências) do cartão → **Rename** (Renomear). O nome é usado nas expressões da [seção 2.4](#24-devolver-a-resposta-ao-worker).
5. Preencha os campos:

| Campo | Valor |
|-------|-------|
| **Post as** (Postar como) | `Flow bot` |
| **Post in** (Postar em) | `Chat with Flow bot` (Chat com o bot de Fluxo) |
| **Recipient** (Destinatário) | o seu e-mail corporativo |
| **Message** (Mensagem) | a expressão abaixo |
| **Update message** (Mensagem de atualização) | `Resposta registrada. Volte ao VS Code para acompanhar a sessão.` |
| **Should update card** (Deve atualizar o cartão) | `Yes` (Sim), para impedir um segundo clique no mesmo cartão |

Para preencher **Message**, clique no campo → ícone **fx** (ou a aba **Expression**/Expressão) → cole a expressão → **Add** (Adicionar):

```text
triggerBody()?['attachments']?[0]?['content']
```

Se o designer recusar o valor ao salvar, use a versão que converte o cartão para texto:

```text
string(triggerBody()?['attachments']?[0]?['content'])
```

> **Por que "Chat with Flow bot"?** O chat consigo mesmo do Teams (o "chat de anotações", `48:notes`) normalmente não aparece como destino dessa ação, e esse caminho não foi comprovado. O chat com o Flow bot também é uma conversa 1:1 só sua, e é ali que os cartões chegam.

6. Ainda na ação `AguardarResposta`, clique nos **...** → **Settings** (Configurações). Em **Timeout** (Tempo limite), coloque `PT15M` (15 minutos, no formato ISO 8601). Use o mesmo valor de `TeamsBridge:RequestTimeToLive` (padrão `00:15:00`). Clique em **Done** (Concluído).

### 2.4 Devolver a resposta ao worker

1. Abaixo de `AguardarResposta`, clique em **+** → **Add an action**.
2. Pesquise `Response` e selecione a ação **Response** do conector **Request** (Solicitação). Ela fica entre as ações internas (*Built-in*).
3. Preencha os campos:

| Campo | Valor |
|-------|-------|
| **Status Code** | `200` |
| **Headers** | chave `Content-Type`, valor `application/json` |
| **Body** | a expressão abaixo, inserida pelo **fx** |

```text
setProperty(setProperty(json('{}'), 'responder', body('AguardarResposta')?['responder']), 'data', body('AguardarResposta')?['data'])
```

A expressão monta exatamente o objeto que o worker espera, `{ "responder": {...}, "data": {...} }`. Montar o JSON digitando texto e tokens no campo **Body** costuma transformar os objetos em texto, e o worker rejeita esse formato.

4. Clique nos **...** da ação **Response** → **Settings** → ligue **Asynchronous response** (Resposta assíncrona) → **Done**.

Com a resposta assíncrona ligada, o Power Automate responde ao worker na hora com `202 Accepted` e um cabeçalho `Location`. O worker consulta essa URL até o fluxo terminar.

> A documentação oficial descreve a resposta assíncrona para o gatilho HTTP. No gatilho de webhook do Teams, isso ainda precisa ser confirmado ([seção 3](#3-testar-o-workflow-sozinho-e-descobrir-seus-ids)). Se a opção não aparecer ou o fluxo falhar ao salvar, anote a mensagem: o modo de retorno do worker terá de mudar.

### 2.5 Salvar e copiar a URL

1. Clique em **Save** (Salvar), no canto superior direito. Corrija os erros que o designer apontar.
2. Clique de novo no gatilho. O campo **HTTP URL** agora mostra a URL do webhook.
3. Copie a URL e guarde-a só no host, no arquivo da [seção 6](#6-configurar-o-worker). **Não cole em chat, ticket, commit ou linha de comando.** Ela contém a assinatura `sig=`.

O fluxo final fica assim:

```text
When a Teams webhook request is received
  └─ AguardarResposta (Post adaptive card in a chat or channel and wait for a response)
       └─ Response (Asynchronous response: On)
```

---

## 3. Testar o Workflow sozinho e descobrir seus IDs

Antes de ligar o worker, confirme que o Workflow funciona e descubra o **object ID** e o **tenant ID** da sua conta, sem usar Microsoft Graph.

### 3.1 Disparar um cartão sintético

No host, crie um diretório temporário protegido e grave a URL nele **sem que ela apareça no terminal**:

```bash
$ install -d -m 700 ~/tmp-workflow-test && cd ~/tmp-workflow-test
$ umask 077
$ cat > url.conf   # cole: url = "https://<url-do-webhook>"   e tecle Ctrl+D
```

Crie o cartão de teste:

```bash
$ cat > card.json <<'EOF'
{
  "type": "message",
  "attachments": [{
    "contentType": "application/vnd.microsoft.card.adaptive",
    "contentUrl": null,
    "content": {
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "type": "AdaptiveCard",
      "version": "1.4",
      "body": [{ "type": "TextBlock", "text": "Teste do CopilotTeamsBridge", "wrap": true }],
      "actions": [{ "type": "Action.Submit", "title": "Confirmar", "data": { "decision": "accept", "correlationToken": "teste" } }]
    }
  }]
}
EOF
$ curl -sS -o /dev/null -D headers.txt -X POST -H "Content-Type: application/json" --data @card.json --config url.conf
$ head -1 headers.txt; grep -ic '^location:' headers.txt
```

A segunda linha conta os cabeçalhos `Location` sem mostrar a URL. Resultados possíveis:

- `HTTP/... 202` e contagem `1`: a resposta assíncrona está funcionando.
- `202` e contagem `0`: a resposta assíncrona não está ativa. Revise a [seção 2.4](#24-devolver-a-resposta-ao-worker).
- `401` ou `403`: a opção **Who can trigger the flow?** não combina com o modo de autenticação. Para este teste, use **Anyone**.

### 3.2 Responder no Teams e consultar o resultado

1. No Teams, abra o chat com o **Workflows**/**Flow bot**. O cartão "Teste do CopilotTeamsBridge" deve aparecer ali. Clique em **Confirmar**.
2. No host, consulte o `Location` sem imprimir a URL:

```bash
$ loc=$(grep -i '^location:' headers.txt | cut -d' ' -f2- | tr -d '\r')
$ curl -sS "$loc" | python3 -m json.tool
```

O resultado esperado é um JSON com `responder` (que traz `objectId` e `tenantId`) e `data` (que traz `decision` e `correlationToken`). Se a resposta ainda for `202`, espere alguns segundos e repita.

### 3.3 Anotar os IDs

Do JSON acima, anote:

- `responder.objectId` → vai em `Responder:AllowedObjectId`;
- `responder.tenantId` → vai em `Responder:AllowedTenantId`.

Se preferir, os mesmos valores aparecem no histórico do fluxo: **My flows** → `CopilotTeamsBridge` → **28-day run history** (Histórico de execuções de 28 dias) → abra a execução → expanda **AguardarResposta** → **Show raw outputs** (Mostrar saídas brutas).

### 3.4 Limpar

```bash
$ cd ~ && rm -rf ~/tmp-workflow-test
```

---

## 4. Preparar o host Linux

### 4.1 Baixar, testar e publicar

```bash
$ git clone https://github.com/lzocateli/CopilotTeamsBridge.git ~/src/CopilotTeamsBridge
$ cd ~/src/CopilotTeamsBridge
$ dotnet test CopilotTeamsBridge.slnx
$ dotnet publish src/CopilotTeamsBridge.Worker -c Release -o ~/apps/copilot-teams-bridge
```

O executável publicado fica em `~/apps/copilot-teams-bridge/copilot-teams-bridge.dll`.

### 4.2 Criar o diretório de segredos

```bash
$ install -d -m 700 ~/.config/copilot-teams-bridge
$ umask 077
$ openssl rand -base64 32 > ~/.config/copilot-teams-bridge/correlation.key
$ chmod 600 ~/.config/copilot-teams-bridge/*
```

O worker se recusa a ler arquivos de segredo que o grupo ou outros usuários possam ler.

---

## 5. Conectar ao Agent Host

O worker precisa de três informações sobre o Agent Host: o **endpoint** (`ws://127.0.0.1:<porta>/`), o **token de conexão** e o **canal do chat** (`ahp-chat:/<id>`).

### 5.1 Onde o Agent Host publica porta e token

Cada Agent Host em execução grava um arquivo de registro em:

```text
~/.config/Code/agent-host/local-endpoint/entries/<id>.json
```

O arquivo traz `endpoint.host`, `endpoint.port` e `connectionToken`. Sem `--port`, a porta é escolhida pelo sistema e **muda a cada reinício**.

### 5.2 Opção recomendada: porta e token fixos

Um token e uma porta fixos deixam a configuração do worker estável. Edite o serviço do Agent Host (exemplo para um serviço de usuário systemd):

```bash
$ openssl rand -hex 32 > ~/.config/copilot-teams-bridge/agent-host.token
$ chmod 600 ~/.config/copilot-teams-bridge/agent-host.token
$ systemctl --user edit --full copilot-agent-host.service
```

Na linha `ExecStart`, acrescente `--port <porta>` e `--connection-token-file %h/.config/copilot-teams-bridge/agent-host.token`. **Não use `--host 0.0.0.0`:** o Agent Host deve continuar ouvindo só em `localhost`.

```bash
$ systemctl --user daemon-reload
$ systemctl --user restart copilot-agent-host.service
```

> O restart interrompe turnos em andamento. Faça isso com a sessão parada; as sessões persistidas continuam disponíveis depois. O VS Code lê o novo token pelo registro local e reconecta.

### 5.3 Alternativa: ler do registro a cada início

Se não puder mexer no serviço, extraia o token para o arquivo e veja só a porta. O comando abaixo usa a entrada `standalone` mais recente e não imprime o token:

```bash
$ python3 - <<'EOF'
import glob, json, os
entries = [json.load(open(f)) for f in glob.glob(os.path.expanduser('~/.config/Code/agent-host/local-endpoint/entries/*.json'))]
entry = max((e for e in entries if e.get('type') == 'standalone'), key=lambda e: e.get('pid', 0))
path = os.path.expanduser('~/.config/copilot-teams-bridge/agent-host.token')
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
os.write(fd, entry['connectionToken'].encode()); os.close(fd)
print('porta:', entry['endpoint']['port'])
EOF
```

Repita esse passo sempre que o Agent Host reiniciar.

### 5.4 Canal do chat

O `ChatChannel` identifica a conversa do Copilot que o worker vai acompanhar, no formato `ahp-chat:/<id>`. **Ainda não existe um procedimento validado para obter esse identificador**: ele faz parte da primeira etapa da [seção 7](#7-validar-em-três-etapas). Até lá, use um valor provisório, por exemplo `ahp-chat:/pendente`, com `DryRun=true`.

---

## 6. Configurar o worker

Crie `~/apps/copilot-teams-bridge/appsettings.Local.json`, no mesmo diretório do binário publicado, com permissão `600`. **É aqui que fica a URL do webhook.**

```bash
$ umask 077
$ nano ~/apps/copilot-teams-bridge/appsettings.Local.json
```

```json
{
  "TeamsBridge": {
    "DryRun": true,
    "CorrelationKeyFile": "/home/<usuario>/.config/copilot-teams-bridge/correlation.key",
    "RequestTimeToLive": "00:15:00",
    "AgentHost": {
      "Endpoint": "ws://127.0.0.1:<porta>/",
      "TokenFile": "/home/<usuario>/.config/copilot-teams-bridge/agent-host.token",
      "ChatChannel": "ahp-chat:/<id-do-chat>"
    },
    "Workflow": {
      "WebhookUrl": "https://<url-do-webhook-copiada-na-secao-2.5>",
      "AllowedPollingHosts": [],
      "AuthMode": "url-secret",
      "BearerTokenFile": ""
    },
    "Responder": {
      "AllowedObjectId": "<objectId-da-secao-3.3>",
      "AllowedTenantId": "<tenantId-da-secao-3.3>"
    }
  }
}
```

```bash
$ chmod 600 ~/apps/copilot-teams-bridge/appsettings.Local.json
```

Observações:

- **`AllowedPollingHosts`:** se ficar vazio, o worker só segue `Location` no mesmo host da `WebhookUrl`. Se o `Location` da [seção 3.1](#31-disparar-um-cartão-sintético) estiver em outro host, coloque esse hostname aqui (só o hostname, sem caminho). Para ver o host sem expor a URL inteira: `grep -i '^location:' headers.txt | cut -d/ -f3`.
- **`AuthMode`:** com **Specific users in my tenant**, use `"bearer"` e aponte `BearerTokenFile` para um arquivo `600` com o token, renovado por um processo externo.
- **Precedência:** valores podem ser sobrescritos por variáveis `TEAMSBRIDGE_*` ou por argumentos `--TeamsBridge:...` (veja o [README](../README.md#configuração)).

---

## 7. Validar em três etapas

Avance para a próxima etapa somente quando a anterior passar. Cada pedido processado gera no log a linha `Pedido <tipo> <fingerprint> finalizado com resultado <resultado>`.

### Etapa 1: DryRun contra a sessão real

Com `"DryRun": true`:

```bash
$ cd ~/apps/copilot-teams-bridge && dotnet copilot-teams-bridge.dll
```

1. Abra a sessão do Copilot no VS Code e peça algo que exija confirmação, por exemplo uma tarefa que rode um comando no terminal.
2. Nos logs do worker, espere ver:
   - `TeamsBridge iniciado (DryRun=True).`;
   - `Conectado ao Agent Host e inscrito no chat configurado.`, sem mensagens repetidas de `Conexao com o Agent Host encerrada`;
   - `... finalizado com resultado DryRun` quando a confirmação aparecer no VS Code.
3. Responda a confirmação **no VS Code**: no DryRun nada é enviado ao Teams.

O que fica comprovado: o worker conecta ao Agent Host e recebe pedidos de um segundo cliente.

### Etapa 2: envio sem resposta

1. Troque para `"DryRun": false` e reinicie o worker.
2. Gere uma nova confirmação no VS Code.
3. O cartão deve aparecer no chat do Flow bot no Teams. **Não clique no Teams**: responda no VS Code.
4. O worker não deve despachar nada; o pedido termina como `RuntimeRequestGone` ou `Expired`.

O que fica comprovado: o cartão chega ao destino certo.

### Etapa 3: prova completa

1. Gere uma nova confirmação no VS Code.
2. Responda **no Teams**.
3. A decisão deve aparecer na **mesma conversa** do VS Code, que continua a tarefa. O log mostra `finalizado com resultado Accepted`.
4. Repita com **recusar** e confirme que a ferramenta não é executada.

O que fica comprovado: o ciclo completo VS Code → Teams → mesma sessão.

---

## 8. Rodar como serviço

Crie `~/.config/systemd/user/copilot-teams-bridge@.service`:

```ini
[Unit]
Description=CopilotTeamsBridge (%i)
After=copilot-agent-host.service

[Service]
WorkingDirectory=%h/apps/copilot-teams-bridge
ExecStart=%h/.dotnet/dotnet %h/apps/copilot-teams-bridge/copilot-teams-bridge.dll --TeamsBridge:AgentHost:ChatChannel=ahp-chat:/%i
Restart=on-failure
RestartSec=10
UMask=0077

[Install]
WantedBy=default.target
```

```bash
$ systemctl --user daemon-reload
$ systemctl --user enable --now copilot-teams-bridge@<id-do-chat>.service
$ journalctl --user -u copilot-teams-bridge@<id-do-chat> -f
$ loginctl enable-linger "$USER"   # mantém os serviços de usuário ativos sem sessão aberta
```

Cada chat acompanhado precisa de uma instância própria: `copilot-teams-bridge@<outro-id>.service`. Se o `dotnet` não estiver em `~/.dotnet`, ajuste o caminho no `ExecStart` (`command -v dotnet`).

---

## 9. Solução de problemas

| Sintoma | Causa provável | O que fazer |
|---------|----------------|-------------|
| Worker não inicia, com mensagem de validação (`Workflow:WebhookUrl deve ser...`) | URL ausente, HTTP ou com fragmento fora do DryRun | Revise `appsettings.Local.json` ([seção 6](#6-configurar-o-worker)). |
| `Chave de correlacao deve ser base64...` | Chave gerada sem base64 | Gere de novo com `openssl rand -base64 32`. |
| Erro de permissão ao ler segredo | Arquivo legível por grupo ou outros | `chmod 600` no arquivo e `chmod 700` no diretório. |
| `Conexao com o Agent Host encerrada` em loop | Porta ou token errados, ou Agent Host reiniciado com porta nova | Refaça a [seção 5](#5-conectar-ao-agent-host). Prefira porta e token fixos. |
| `Entrega ao Workflow sem retorno utilizavel: Rejected (HTTP 401/403)` | **Who can trigger the flow?** não combina com `AuthMode` | **Anyone** usa `url-secret`; **Specific users** usa `bearer` com token válido. |
| `... AcceptedWithoutReturn ...` | Resposta assíncrona desligada ou `Location` em host não aprovado | Ligue **Asynchronous response** ([seção 2.4](#24-devolver-a-resposta-ao-worker)) ou inclua o host em `AllowedPollingHosts`. |
| `... Unavailable (HTTP 429/5xx)` ou resultado `DeliveryUnavailable` | Limite ou falha temporária do Power Automate | Veja o histórico de execuções do fluxo e gere o pedido de novo no VS Code. |
| Cartão não aparece no Teams | App Workflows não instalado, destinatário errado ou fluxo desligado | Confira os [pré-requisitos](#1-pré-requisitos), o campo **Recipient** e se o fluxo está **On** em **My flows**. |
| Resultado `Forbidden` | Resposta de outra conta ou tenant | Confira `AllowedObjectId`/`AllowedTenantId` ([seção 3.3](#33-anotar-os-ids)). |
| Resultado `Expired` ou `Duplicate` | Cartão respondido depois do prazo ou mais de uma vez | Ajuste `RequestTimeToLive` e o `Timeout` da [seção 2.3](#23-publicar-o-cartão-e-esperar-a-resposta), mantendo os dois iguais. |
| Erro ao salvar a ação **Response** | Resposta assíncrona indisponível nesse gatilho | Registre a mensagem: é uma das [limitações](../README.md#limitações-ainda-não-comprovadas) em validação. |

---

## Cuidados de segurança

- Trate a URL do webhook, os tokens e a chave de correlação como senhas. Eles ficam só no host, em arquivos `600`.
- O Agent Host nunca deve ouvir fora de `localhost`, e o worker não abre portas.
- O worker nunca aprova sozinho. Cada decisão precisa vir de uma resposta sua, com token de correlação válido e não expirado.
- Para revogar o acesso imediatamente, desligue o fluxo em **My flows** → `CopilotTeamsBridge` → **Turn off** (Desativar). Também é possível excluir o gatilho, o que gera uma nova URL.
