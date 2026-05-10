# Come funziona questo progetto — M365 Agents SDK

Guida pratica per chi conosce già Foundry Hosted Agents e vuole capire M365 Agents SDK partendo da basi solide.

---

## 1. Analogia con Foundry Hosted Agent

Con **Foundry Hosted Agent** scrivi il tuo codice, lo impacchetti in un container Docker, lo pusho su ACR, e Foundry lo esegue esponendo un endpoint che parla il protocollo OpenAI Responses API (`POST /responses` su porta 8088).

Con **M365 Agents SDK** scrivi il tuo codice .NET, lo pubblichi su **Azure App Service** come ZIP package (senza Docker), e il framework espone un endpoint che parla il **Bot Framework Activity Protocol** (`POST /api/messages` su porta 3978).

In entrambi i casi stai scrivendo **codice custom** che gira su infrastruttura Azure — non un prompt agent gestito da un servizio.

| | Foundry Hosted Agent | M365 Agents SDK |
|---|---|---|
| Impacchettamento | Dockerfile → ACR push | `dotnet publish` → ZIP |
| Runtime | Docker container (ACA-like) | Azure App Service |
| Protocollo wire | OpenAI Responses API | Bot Framework Activity JSON |
| "Broker" canali | Azure Bot Service (wizard Foundry) | Azure Bot Service (Bicep) |
| Test locale diretto | `curl localhost:8088` | Bot FW Emulator / M365 Agents Playground |
| Canali Microsoft 365 | ✅ (via Bot Service) | ✅ nativo |

---

## 2. Il protocollo wire: Activity JSON

Non puoi chiamare `/api/messages` con un semplice `curl` come faresti con le Responses API. L'endpoint parla il **Bot Framework Activity Protocol** — un JSON strutturato che descrive ogni evento della conversazione:

```json
POST http://localhost:3978/api/messages
{
  "type": "message",
  "text": "ciao",
  "from": { "id": "user1", "name": "Mauro" },
  "recipient": { "id": "bot", "name": "ZavaAgent" },
  "conversation": { "id": "conv123" },
  "channelId": "emulator",
  "serviceUrl": "http://localhost:56150"
}
```

Il campo `serviceUrl` è fondamentale: è l'indirizzo a cui il tuo bot dovrà rispondere (vedi sezione 8).

---

## 3. Dove gira il codice in produzione

Il Bicep in `infra/` crea questa architettura su Azure:

```
infra/modules/
├── webapp.bicep      →  App Service Plan + Web App  ← il tuo .NET 9 qui
├── botservice.bicep  →  Azure Bot Service  ← bridge verso Teams/M365
├── identity.bicep    →  Managed Identity
├── storage.bicep     →  Blob Storage (stato conversazioni)
├── keyvault.bicep    →  Segreti
└── monitoring.bicep  →  Application Insights + Log Analytics
```

Il deploy avviene come **ZIP package** (non container), configurato da `WEBSITE_RUN_FROM_PACKAGE: '1'` in `webapp.bicep`. App Service monta il ZIP come filesystem virtuale read-only su `D:\home\site\wwwroot` — nessuna estrazione su disco, avvio più veloce.

Il **Azure Bot Service** (`botservice.bicep`) è configurato con:
```
endpoint: 'https://${webAppDefaultHostName}/api/messages'
```
È lui che fa da bridge tra Teams/M365 Copilot e il tuo App Service.

---

## 4. Il devtunnel in sviluppo locale

Azure Bot Service è un servizio cloud. Quando Teams manda un messaggio, Bot Service fa una chiamata **in uscita** verso il tuo endpoint. Se il tuo endpoint è `localhost:3978`, Bot Service non ci può arrivare.

Il **devtunnel** risolve questo creando un tunnel tra la tua macchina e un hostname pubblico su `*.devtunnels.ms`:

```
Teams → Azure Bot Service → https://abc123-3978.euw.devtunnels.ms/api/messages
                                          ↓ tunnel
                                    devtunnel CLI sulla tua macchina
                                          ↓
                                    localhost:3978
```

Lo script `infra/scripts/devtunnel.sh` crea il tunnel, apre la porta 3978 con accesso pubblico, e scrive `BOT_ENDPOINT` e `BOT_DOMAIN` nel file `env/.env.local` — usati poi dal task `Provision` per configurare il Bot Service con l'URL pubblico corretto.

**Alternativa senza devtunnel**: il **M365 Agents Playground** (task in VS Code) simula Bot Service localmente sulla porta 56150, senza uscire su internet.

---

## 5. Registrazione delle dipendenze (DI)

In `Program.cs` vengono registrati tutti gli oggetti nel container DI di ASP.NET:

```csharp
// IStorage: stato delle conversazioni — Singleton
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// IChatClient: client verso Azure OpenAI — Singleton
// Usa una factory lambda perché la costruzione richiede configurazione runtime.
// "sp" è l'IServiceProvider (il container DI stesso), usato per risolvere IConfiguration.
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    return azureClient.GetChatClient(deployment).AsIChatClient();
    //                               ↑ AsIChatClient() è un extension method di
    //                                 Microsoft.Extensions.AI che wrappa il ChatClient
    //                                 nell'interfaccia standard, rendendo il codice
    //                                 agnostico rispetto al provider LLM
});

// ZavaInsuranceAgent: l'agente — Transient (nuova istanza ad ogni richiesta)
// Internamente equivale a: AddTransient<IAgent, ZavaInsuranceAgent>()
builder.AddAgent<ZavaInsuranceAgent>();
```

**Singleton** = una sola istanza per tutta la vita dell'app, creata la prima volta che serve.  
**Transient** = nuova istanza ad ogni richiesta.

Il container DI è come un dizionario:
```
IStorage          →  MemoryStorage         (Singleton)
IAgentHttpAdapter →  CloudAdapter          (Singleton)  ← registrato da AddAgent
IChatClient       →  AzureOpenAI wrapper   (Singleton)
IAgent            →  ZavaInsuranceAgent    (Transient)  ← registrato da AddAgent
```

Quando ASP.NET deve risolvere `IAgent`, guarda in questo dizionario e trova `ZavaInsuranceAgent`.

---

## 6. Il CloudAdapter — il portiere dell'applicazione

Il `CloudAdapter` è lo strato che si occupa di tutto quello che sta tra la rete e il tuo codice applicativo, in entrambe le direzioni. Il tuo `ZavaInsuranceAgent` non sa nulla di JWT, HTTP, o Bot Service — parla solo con `ITurnContext`. Il `CloudAdapter` si prende cura di tutto il resto.

```
Bot Service (HTTP)
      ↕
  CloudAdapter   ← tutto ciò che è "protocollo", "rete", "auth"
      ↕
ZavaInsuranceAgent (C# puro)  ← solo logica applicativa
```

### In ingresso (Bot Service → tuo bot)

Quando arriva una POST a `/api/messages`:

1. **Controlla il biglietto** — valida il JWT firmato da Bot Service. Se non è valido, rifiuta subito con 401.
2. **Apre il pacco** — deserializza il body HTTP (JSON) nell'oggetto `Activity` tipizzato.
3. **Risponde subito alla porta** — manda 202 Accepted a Bot Service e chiude la connessione HTTP. Bot Service è soddisfatto — sa che hai ricevuto il messaggio.
4. **Passa il lavoro in background** — mette l'Activity in una coda interna e la passa a un hosted service che la elaborerà su un thread separato.

### In uscita (tuo bot → Bot Service)

Quando il tuo handler chiama `QueueTextChunk` o `EndStreamAsync`:

1. **Prende un token di accesso** — si autentica verso Bot Service usando la Managed Identity.
2. **Fa la chiamata HTTP in uscita** — POST al `serviceUrl` dell'Activity ricevuta, con il chunk di risposta.

### Perché si chiama "Cloud"

Esiste anche un `LocalAdapter` usato in certi scenari di test. Il prefisso "Cloud" indica che è progettato per operare con **Azure Bot Service nel cloud** — gestisce autenticazione OAuth, certificati, e il modello asincrono a callback che Bot Service richiede.

---

## 7. Cosa passa nella chiamata a /api/messages

### La richiesta HTTP grezza

Bot Service manda esattamente questo formato:

```http
POST /api/messages HTTP/1.1
Host: tuoapp.azurewebsites.net
Content-Type: application/json
Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGc...  ← JWT firmato da Bot Service

{
  "type": "message",
  "text": "ciao",
  "from": { "id": "user1", "name": "Mauro" },
  "recipient": { "id": "bot", "name": "ZavaAgent" },
  "conversation": { "id": "conv123", "isGroup": false },
  "channelId": "msteams",
  "serviceUrl": "https://smba.trafficmanager.net/amer/",
  "id": "1715123456789",
  "timestamp": "2026-05-10T10:30:00.000Z"
}
```

### Come si valorizzano i parametri del Minimal API endpoint

```csharp
app.MapPost("/api/messages", async (
    HttpRequest  request,           // [A]
    HttpResponse response,          // [B]
    IAgentHttpAdapter adapter,      // [C]
    IAgent agent,                   // [D]
    CancellationToken cancellationToken  // [E]
) => { ... });
```

**[A] `HttpRequest request`** — wrappa la richiesta HTTP grezza. Contiene `request.Body` (lo stream JSON dell'Activity), `request.Headers["Authorization"]` (il JWT), `request.Method`. Viene iniettato dal framework HTTP di ASP.NET dal contesto della richiesta corrente — non dal DI.

**[B] `HttpResponse response`** — l'oggetto su cui `CloudAdapter` scriverà il 202 Accepted. Anche questo viene dal contesto HTTP, non dal DI.

**[C] `IAgentHttpAdapter adapter`** — risolto dal DI container → istanza Singleton di `CloudAdapter` già costruita all'avvio. Non ha nulla a che fare con il contenuto della richiesta — è sempre lo stesso oggetto.

**[D] `IAgent agent`** — risolto dal DI container → nuova istanza Transient di `ZavaInsuranceAgent` costruita ora. Nessuna relazione con il contenuto della richiesta — il DI costruisce l'oggetto basandosi solo sulla registrazione `AddAgent<ZavaInsuranceAgent>()`.

**[E] `CancellationToken cancellationToken`** — fornito da ASP.NET, si attiva se il client disconnette o se l'app si sta spegnendo. Non viene dal DI né dalla richiesta.

### Il punto chiave: i parametri sono pronti prima di leggere il body

Quando l'endpoint viene invocato, tutti i parametri sono già valorizzati **prima** che `ProcessAsync` legga anche un solo byte del body JSON:

```
HTTP POST arriva
      ↓
ASP.NET costruisce HttpRequest e HttpResponse  (dal contesto HTTP)
ASP.NET risolve CloudAdapter dal DI            (Singleton, già esisteva)
ASP.NET risolve ZavaInsuranceAgent dal DI      (Transient, creato ora)
ASP.NET crea CancellationToken                 (dal contesto HTTP)
      ↓
chiama il tuo handler con questi 5 parametri già pronti
      ↓
adapter.ProcessAsync(request, response, agent, ct)
      ↓  solo qui CloudAdapter legge request.Body
      legge il JWT dall'header Authorization
      deserializza il JSON → oggetto Activity
      usa agent per il processing
```

`ZavaInsuranceAgent` viene costruito **senza sapere nulla** di cosa c'è nel body. Il contenuto del messaggio (`"ciao"`, `conversationId`, `serviceUrl`, ecc.) diventa visibile al tuo codice solo dopo, dentro l'handler, attraverso `turnContext.Activity`.

---

## 8. Il flusso completo da messaggio utente a risposta

### Passo 1 — L'utente scrive in Teams

Teams manda il messaggio ad Azure Bot Service.

### Passo 2 — Bot Service chiama il tuo endpoint

Bot Service costruisce un oggetto `Activity` (JSON strutturato) e fa una POST a `https://tuoapp.azurewebsites.net/api/messages`.

### Passo 3 — ASP.NET riceve la richiesta

Il Minimal API endpoint si attiva:

```csharp
app.MapPost("/api/messages", async (
    HttpRequest request,
    HttpResponse response,
    IAgentHttpAdapter adapter,  // ← DI risolve: CloudAdapter (Singleton)
    IAgent agent,               // ← DI risolve: crea nuova istanza ZavaInsuranceAgent (Transient)
    CancellationToken ct) =>
{
    await adapter.ProcessAsync(request, response, agent, ct);
});
```

ASP.NET risolve automaticamente i parametri dal container DI. `ZavaInsuranceAgent` viene costruito con tutte le sue dipendenze iniettate nel costruttore.

### Passo 4 — CloudAdapter risponde subito 202

`CloudAdapter.ProcessAsync`:
- Valida il JWT firmato da Bot Service
- Deserializza il body HTTP → oggetto `Activity`
- Risponde **202 Accepted** al Bot Service — **la connessione HTTP si chiude qui**

### Passo 5 — Background thread elabora

`CloudAdapter` mette l'Activity in una coda interna. Un hosted service la prende e costruisce `ITurnContext` — il "pacchetto" che contiene tutto: l'Activity, i dati della conversazione, il canale di risposta.

### Passo 6 — Il framework carica lo stato

Prima di invocare qualsiasi handler, carica da `IStorage` (Blob Storage in produzione) lo stato della conversazione, incluso `conversation.threadInfo` con la history LLM dei messaggi precedenti.

### Passo 7 — Il router trova l'handler giusto

`AgentApplication` scorre i handler registrati nell'ordine in cui sono stati aggiunti nel costruttore, e si ferma al primo che fa match:

```
Activity.Type == "conversationUpdate" + MembersAdded  →  WelcomeMessageAsync
Activity.Type == "message" + Text == "-reset"         →  Reset  (via [Route])
Activity.Type == "message"                            →  OnMessageAsync  (catch-all)
```

### Passo 8 — La risposta torna all'utente via callback

L'handler NON risponde sulla connessione HTTP originale (già chiusa). Il bot fa **chiamate HTTP in uscita** verso il `serviceUrl` dell'Activity:

```csharp
// Manda subito "sto elaborando..." → POST a serviceUrl
await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Processing your request...");

// Ogni chunk LLM → POST a serviceUrl
turnContext.StreamingResponse.QueueTextChunk(response.Text);

// Fine → POST finale a serviceUrl
await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
```

Bot Service riceve questi chunk e li recapita a Teams, che aggiorna il messaggio progressivamente — identico allo streaming, ma tramite POST HTTP multiple invece di SSE.

### Passo 9 — Stato salvato, istanza distrutta

Il framework salva `ITurnState` aggiornato su `IStorage`. L'istanza di `ZavaInsuranceAgent` viene distrutta — era Transient, il suo lavoro è finito.

---

## 9. I tipi di Activity

`/api/messages` viene chiamato ad **ogni Activity** — non solo quando l'utente scrive un messaggio:

| Tipo Activity | Quando scatta | Handler in questo progetto |
|---|---|---|
| `conversationUpdate` + MembersAdded | Qualcuno entra nella conversazione | `WelcomeMessageAsync` |
| `message` + testo `-reset` | Utente scrive `-reset` | `Reset` (via `[Route]`) |
| `message` | Qualsiasi altro messaggio | `OnMessageAsync` (catch-all) |
| `invoke` | Click su pulsante Adaptive Card | — |
| `messageReaction` | Reaction in Teams | — |

Ogni Activity crea una nuova istanza di `ZavaInsuranceAgent` (Transient). La continuità multi-turn non viene dalla persistenza dell'istanza, ma da `ITurnState` caricato da `IStorage` ad ogni turn.

---

## 10. I tre meccanismi di registrazione degli handler

```csharp
public ZavaInsuranceAgent(...) : base(options)
{
    // Meccanismo 1: OnConversationUpdate — handler per eventi del ciclo di vita
    OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeMessageAsync);

    // Meccanismo 2: OnActivity — catch-all per tipo di activity
    // DEVE essere registrato per ultimo: AgentApplication si ferma al primo match
    OnActivity(ActivityTypes.Message, OnMessageAsync);

    // Meccanismo 3: [Route] attribute — dichiarativo via reflection
    // ApplyRouteAttributes() (chiamato dalla base class) scansiona i metodi
    // decorati con [Route] e li registra automaticamente
}

[Route(RouteType = RouteType.Message, Type = ActivityTypes.Message, Text = "-reset")]
protected async Task Reset(ITurnContext turnContext, ITurnState turnState, CancellationToken ct)
{ ... }
```

---

## 11. Lo stato della conversazione e la history LLM

Sono due livelli sovrapposti, non storage separati:

```
ITurnState  (stato del framework, persistito su IStorage)
    └── ConversationState
            └── "conversation.threadInfo"  ← chiave custom
                    └── AgentThread serializzato (JSON)
                            └── messaggi chat: user / assistant / tool
```

La history LLM è **dentro** `ITurnState`, serializzata come JSON in una chiave del `ConversationState`.

```csharp
// Fine di ogni turn: salva
turnState.Conversation.SetValue("conversation.threadInfo",
    ProtocolJsonSerializer.ToJson(thread.Serialize()));

// Inizio del turn successivo: carica
string? agentThreadInfo = turnState.Conversation
    .GetValue<string?>("conversation.threadInfo", () => null);
```

**Storage fisico:**
- Locale/dev: `MemoryStorage` — RAM del processo, persa al riavvio
- Produzione: `BlobsStorage` — Azure Blob Storage (StorageV2, Standard_LRS), un blob JSON per conversazione

**Il reasoning dell'LLM non viene mai persistito.** I "pensieri intermedi" non escono dalle API OpenAI — solo il testo finale della risposta è accessibile e salvabile. Quello che permette la continuità è la **chat history** (messaggi user/assistant/tool), non il reasoning.

`MessageCountingChatReducer(15)` mantiene al massimo 15 messaggi nella history prima di ridurla — quando la conversazione supera questo limite, i messaggi più vecchi vengono eliminati.

---

## 12. Schema riassuntivo

```
Teams / M365 Copilot
        ↓
Azure Bot Service  (bridge cloud, gestisce canali)
        ↓  POST Activity JSON
App Service  →  /api/messages
        ↓
ASP.NET DI crea ZavaInsuranceAgent (Transient)
        ↓
CloudAdapter valida JWT + deserializza Activity
        ↓  202 Accepted  ← connessione HTTP chiusa
        ↓
Background thread
        ↓
Carica ITurnState da Blob Storage  (history LLM inclusa)
        ↓
Router → handler giusto (WelcomeMessageAsync / OnMessageAsync / Reset)
        ↓
Handler → ChatClientAgent → IChatClient → Azure OpenAI
        ↓  streaming chunks
QueueTextChunk → POST a serviceUrl → Bot Service → Teams
        ↓
Salva ITurnState aggiornato su Blob Storage
        ↓
ZavaInsuranceAgent distrutta (era Transient)
```
