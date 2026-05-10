# How This Project Works — M365 Agents SDK

A practical guide for developers already familiar with Foundry Hosted Agents who want to understand M365 Agents SDK from a solid foundation.

---

## 1. Analogy with Foundry Hosted Agent

With a **Foundry Hosted Agent** you write your code, package it in a Docker container, push it to ACR, and Foundry runs it exposing an endpoint that speaks the OpenAI Responses API protocol (`POST /responses` on port 8088).

With **M365 Agents SDK** you write your .NET code, publish it to **Azure App Service** as a ZIP package (no Docker), and the framework exposes an endpoint that speaks the **Bot Framework Activity Protocol** (`POST /api/messages` on port 3978).

In both cases you are writing **custom code** running on Azure infrastructure — not a prompt agent managed by a service.

| | Foundry Hosted Agent | M365 Agents SDK |
|---|---|---|
| Packaging | Dockerfile → ACR push | `dotnet publish` → ZIP |
| Runtime | Docker container (ACA-like) | Azure App Service |
| Wire protocol | OpenAI Responses API | Bot Framework Activity JSON |
| Channel broker | Azure Bot Service (Foundry wizard) | Azure Bot Service (Bicep) |
| Direct local test | `curl localhost:8088` | Bot FW Emulator / M365 Agents Playground |
| Microsoft 365 channels | ✅ (via Bot Service) | ✅ native |

---

## 2. The Wire Protocol: Activity JSON

You cannot call `/api/messages` with a simple `curl` as you would with the Responses API. The endpoint speaks the **Bot Framework Activity Protocol** — a structured JSON that describes every conversation event:

```json
POST http://localhost:3978/api/messages
{
  "type": "message",
  "text": "hello",
  "from": { "id": "user1", "name": "Mauro" },
  "recipient": { "id": "bot", "name": "ZavaAgent" },
  "conversation": { "id": "conv123" },
  "channelId": "emulator",
  "serviceUrl": "http://localhost:56150"
}
```

The `serviceUrl` field is critical: it is the address the bot will use to send its reply back (see section 8).

---

## 3. Where the Code Runs in Production

The Bicep files in `infra/` provision this architecture on Azure:

```
infra/modules/
├── webapp.bicep      →  App Service Plan + Web App  ← your .NET 9 code runs here
├── botservice.bicep  →  Azure Bot Service  ← bridge to Teams/M365
├── identity.bicep    →  Managed Identity
├── storage.bicep     →  Blob Storage (conversation state)
├── keyvault.bicep    →  Secrets
└── monitoring.bicep  →  Application Insights + Log Analytics
```

Deployment uses a **ZIP package** (not a container), configured by `WEBSITE_RUN_FROM_PACKAGE: '1'` in `webapp.bicep`. App Service mounts the ZIP as a read-only virtual filesystem at `D:\home\site\wwwroot` — no extraction to disk, faster startup.

**Azure Bot Service** (`botservice.bicep`) is configured with:
```
endpoint: 'https://${webAppDefaultHostName}/api/messages'
```
It acts as the bridge between Teams/M365 Copilot and your App Service.

---

## 4. The Devtunnel in Local Development

Azure Bot Service is a cloud service. When Teams sends a message, Bot Service makes an **outbound call** to your endpoint. If your endpoint is `localhost:3978`, Bot Service cannot reach it.

The **devtunnel** solves this by creating a tunnel between your machine and a public hostname on `*.devtunnels.ms`:

```
Teams → Azure Bot Service → https://abc123-3978.euw.devtunnels.ms/api/messages
                                          ↓ tunnel
                                    devtunnel CLI on your machine
                                          ↓
                                    localhost:3978
```

The script `infra/scripts/devtunnel.sh` creates the tunnel, opens port 3978 with public access, and writes `BOT_ENDPOINT` and `BOT_DOMAIN` to `env/.env.local` — used by the `Provision` task to configure Bot Service with the correct public URL.

**Alternative without devtunnel**: the **M365 Agents Playground** (VS Code task) simulates Bot Service locally on port 56150, without going out to the internet.

---

## 5. Dependency Registration (DI)

In `Program.cs`, all objects are registered in the ASP.NET DI container:

```csharp
// IStorage: conversation state — Singleton
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// IChatClient: Azure OpenAI client — Singleton
// Uses a factory lambda because construction requires runtime configuration.
// "sp" is the IServiceProvider (the DI container itself), used to resolve IConfiguration.
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
    return azureClient.GetChatClient(deployment).AsIChatClient();
    //                               ↑ AsIChatClient() is an extension method from
    //                                 Microsoft.Extensions.AI that wraps ChatClient
    //                                 into the standard interface, making the code
    //                                 provider-agnostic
});

// ZavaInsuranceAgent: the agent — Transient (new instance per request)
// Internally equivalent to: AddTransient<IAgent, ZavaInsuranceAgent>()
builder.AddAgent<ZavaInsuranceAgent>();
```

**Singleton** = one instance for the entire lifetime of the app, created the first time it is needed.  
**Transient** = a new instance for every request.

The DI container works like a dictionary:
```
IStorage          →  MemoryStorage         (Singleton)
IAgentHttpAdapter →  CloudAdapter          (Singleton)  ← registered by AddAgent
IChatClient       →  AzureOpenAI wrapper   (Singleton)
IAgent            →  ZavaInsuranceAgent    (Transient)  ← registered by AddAgent
```

When ASP.NET needs to resolve `IAgent`, it looks up this dictionary and finds `ZavaInsuranceAgent`.

---

## 6. The CloudAdapter — The Doorman

The `CloudAdapter` is the layer that handles everything between the network and your application code, in both directions. Your `ZavaInsuranceAgent` knows nothing about JWT, HTTP, or Bot Service — it only talks to `ITurnContext`. The `CloudAdapter` takes care of everything else.

```
Bot Service (HTTP)
      ↕
  CloudAdapter   ← all protocol, networking, and auth concerns
      ↕
ZavaInsuranceAgent (pure C#)  ← only application logic
```

### Inbound (Bot Service → your bot)

When a POST arrives at `/api/messages`:

1. **Checks the ticket** — validates the JWT signed by Bot Service. If invalid, rejects immediately with 401.
2. **Unpacks the payload** — deserializes the HTTP body (JSON) into a typed `Activity` object.
3. **Immediately closes the door** — sends 202 Accepted to Bot Service and closes the HTTP connection. Bot Service is satisfied — it knows you received the message.
4. **Passes the work to background** — puts the Activity in an internal queue and hands it off to a hosted service that will process it on a separate thread.

### Outbound (your bot → Bot Service)

When your handler calls `QueueTextChunk` or `EndStreamAsync`:

1. **Gets an access token** — authenticates against Bot Service using the Managed Identity.
2. **Makes an outbound HTTP call** — POST to the `serviceUrl` from the received Activity, carrying the response chunk.

### Why It Is Called "Cloud"

A `LocalAdapter` also exists for certain test scenarios. The "Cloud" prefix indicates it is designed to work with **Azure Bot Service in the cloud** — handling OAuth authentication, certificates, and the asynchronous callback model that Bot Service requires.

---

## 7. What Bot Service Sends to /api/messages

### The Raw HTTP Request

Bot Service sends exactly this format:

```http
POST /api/messages HTTP/1.1
Host: yourapp.azurewebsites.net
Content-Type: application/json
Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGc...  ← JWT signed by Bot Service

{
  "type": "message",
  "text": "hello",
  "from": { "id": "user1", "name": "Mauro" },
  "recipient": { "id": "bot", "name": "ZavaAgent" },
  "conversation": { "id": "conv123", "isGroup": false },
  "channelId": "msteams",
  "serviceUrl": "https://smba.trafficmanager.net/amer/",
  "id": "1715123456789",
  "timestamp": "2026-05-10T10:30:00.000Z"
}
```

### How the Minimal API Endpoint Parameters Are Resolved

```csharp
app.MapPost("/api/messages", async (
    HttpRequest  request,           // [A]
    HttpResponse response,          // [B]
    IAgentHttpAdapter adapter,      // [C]
    IAgent agent,                   // [D]
    CancellationToken cancellationToken  // [E]
) => { ... });
```

**[A] `HttpRequest request`** — wraps the raw HTTP request. Contains `request.Body` (the Activity JSON stream), `request.Headers["Authorization"]` (the JWT), `request.Method`. Injected by the ASP.NET HTTP framework from the current request context — not from DI.

**[B] `HttpResponse response`** — the object on which `CloudAdapter` will write 202 Accepted. Also comes from the HTTP context, not from DI.

**[C] `IAgentHttpAdapter adapter`** — resolved from the DI container → Singleton `CloudAdapter` instance already built at startup. Nothing to do with the request content — it is always the same object.

**[D] `IAgent agent`** — resolved from the DI container → new Transient `ZavaInsuranceAgent` instance created right now. No relation to the request content — DI builds the object based solely on the `AddAgent<ZavaInsuranceAgent>()` registration.

**[E] `CancellationToken cancellationToken`** — provided by ASP.NET, fires if the client disconnects or the app is shutting down. Does not come from DI or from the request.

### The Key Point: Parameters Are Ready Before the Body Is Read

When the endpoint is invoked, all parameters are already resolved **before** `ProcessAsync` reads even a single byte of the JSON body:

```
HTTP POST arrives
      ↓
ASP.NET builds HttpRequest and HttpResponse  (from HTTP context)
ASP.NET resolves CloudAdapter from DI        (Singleton, already existed)
ASP.NET resolves ZavaInsuranceAgent from DI  (Transient, created now)
ASP.NET creates CancellationToken            (from HTTP context)
      ↓
calls your handler with these 5 parameters already ready
      ↓
adapter.ProcessAsync(request, response, agent, ct)
      ↓  only here does CloudAdapter read request.Body
      reads the JWT from the Authorization header
      deserializes the JSON → Activity object
      uses agent for processing
```

`ZavaInsuranceAgent` is constructed **knowing nothing** about what is in the body. The message content (`"hello"`, `conversationId`, `serviceUrl`, etc.) becomes visible to your code only later, inside the handler, through `turnContext.Activity`.

---

## 8. The Complete Flow from User Message to Response

### Step 1 — User writes in Teams

Teams sends the message to Azure Bot Service.

### Step 2 — Bot Service calls your endpoint

Bot Service builds an `Activity` object (structured JSON) and POSTs to `https://yourapp.azurewebsites.net/api/messages`.

### Step 3 — ASP.NET receives the request

The Minimal API endpoint activates:

```csharp
app.MapPost("/api/messages", async (
    HttpRequest request,
    HttpResponse response,
    IAgentHttpAdapter adapter,  // ← DI resolves: CloudAdapter (Singleton)
    IAgent agent,               // ← DI resolves: new ZavaInsuranceAgent instance (Transient)
    CancellationToken ct) =>
{
    await adapter.ProcessAsync(request, response, agent, ct);
});
```

ASP.NET automatically resolves parameters from the DI container. `ZavaInsuranceAgent` is constructed with all its dependencies injected into the constructor.

### Step 4 — CloudAdapter immediately responds 202

`CloudAdapter.ProcessAsync`:
- Validates the JWT signed by Bot Service
- Deserializes the HTTP body → `Activity` object
- Responds **202 Accepted** to Bot Service — **the HTTP connection closes here**

### Step 5 — Background thread processes

`CloudAdapter` puts the Activity into an internal queue. A hosted service picks it up and builds `ITurnContext` — the "package" that contains everything: the Activity, conversation data, and the reply channel.

### Step 6 — Framework loads state

Before invoking any handler, it loads from `IStorage` (Blob Storage in production) the conversation state — including `conversation.threadInfo` with the LLM message history from previous turns.

### Step 7 — Router finds the right handler

`AgentApplication` walks the list of registered handlers in the order they were added in the constructor, stopping at the first match:

```
Activity.Type == "conversationUpdate" + MembersAdded  →  WelcomeMessageAsync
Activity.Type == "message" + Text == "-reset"         →  Reset  (via [Route])
Activity.Type == "message"                            →  OnMessageAsync  (catch-all)
```

### Step 8 — The reply reaches the user via callback

The handler does NOT reply on the original HTTP connection (already closed). The bot makes **outbound HTTP calls** to the `serviceUrl` from the Activity:

```csharp
// Immediately sends "processing..." → POST to serviceUrl
await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Processing your request...");

// Each LLM chunk → POST to serviceUrl
turnContext.StreamingResponse.QueueTextChunk(response.Text);

// Done → final POST to serviceUrl
await turnContext.StreamingResponse.EndStreamAsync(cancellationToken);
```

Bot Service receives these chunks and delivers them to Teams, which updates the message progressively — identical to streaming, but via multiple HTTP POSTs instead of SSE.

### Step 9 — State saved, instance destroyed

The framework saves the updated `ITurnState` to `IStorage`. The `ZavaInsuranceAgent` instance is destroyed — it was Transient, its job is done.

---

## 9. Activity Types

`/api/messages` is called for **every Activity** — not just when the user sends a text message:

| Activity Type | When it fires | Handler in this project |
|---|---|---|
| `conversationUpdate` + MembersAdded | Someone joins the conversation | `WelcomeMessageAsync` |
| `message` + text `-reset` | User types `-reset` | `Reset` (via `[Route]`) |
| `message` | Any other message | `OnMessageAsync` (catch-all) |
| `invoke` | Adaptive Card button click | — |
| `messageReaction` | Reaction in Teams | — |

Each Activity creates a new `ZavaInsuranceAgent` instance (Transient). Multi-turn continuity does not come from a persistent agent instance — it comes from `ITurnState` loaded from `IStorage` at the start of each turn.

---

## 10. The Three Handler Registration Mechanisms

```csharp
public ZavaInsuranceAgent(...) : base(options)
{
    // Mechanism 1: OnConversationUpdate — handler for lifecycle events
    OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeMessageAsync);

    // Mechanism 2: OnActivity — catch-all for an activity type
    // MUST be registered last: AgentApplication stops at the first match
    OnActivity(ActivityTypes.Message, OnMessageAsync);

    // Mechanism 3: [Route] attribute — declarative via reflection
    // ApplyRouteAttributes() (called by the base class) scans methods
    // decorated with [Route] and registers them automatically
}

[Route(RouteType = RouteType.Message, Type = ActivityTypes.Message, Text = "-reset")]
protected async Task Reset(ITurnContext turnContext, ITurnState turnState, CancellationToken ct)
{ ... }
```

---

## 11. Conversation State and LLM History

These are two nested layers, not separate storage systems:

```
ITurnState  (framework state, persisted to IStorage)
    └── ConversationState
            └── "conversation.threadInfo"  ← custom key
                    └── serialized AgentThread (JSON)
                            └── chat messages: user / assistant / tool
```

The LLM history is **inside** `ITurnState`, serialized as JSON under a key in `ConversationState`.

```csharp
// End of each turn: save
turnState.Conversation.SetValue("conversation.threadInfo",
    ProtocolJsonSerializer.ToJson(thread.Serialize()));

// Start of the next turn: load
string? agentThreadInfo = turnState.Conversation
    .GetValue<string?>("conversation.threadInfo", () => null);
```

**Physical storage:**
- Local/dev: `MemoryStorage` — process RAM, lost on restart
- Production: `BlobsStorage` — Azure Blob Storage (StorageV2, Standard_LRS), one JSON blob per conversation

**LLM reasoning is never persisted.** Intermediate "thoughts" never leave the OpenAI APIs — only the final response text is accessible and saveable. What enables multi-turn continuity is the **chat history** (user/assistant/tool messages), not the reasoning.

`MessageCountingChatReducer(15)` keeps at most 15 messages in the history before trimming it — once a conversation exceeds this limit, older messages are dropped.

---

## 12. Summary Diagram

```
Teams / M365 Copilot
        ↓
Azure Bot Service  (cloud bridge, manages channels)
        ↓  POST Activity JSON
App Service  →  /api/messages
        ↓
ASP.NET DI creates ZavaInsuranceAgent (Transient)
        ↓
CloudAdapter validates JWT + deserializes Activity
        ↓  202 Accepted  ← HTTP connection closed
        ↓
Background thread
        ↓
Loads ITurnState from Blob Storage  (LLM history included)
        ↓
Router → right handler (WelcomeMessageAsync / OnMessageAsync / Reset)
        ↓
Handler → ChatClientAgent → IChatClient → Azure OpenAI
        ↓  streaming chunks
QueueTextChunk → POST to serviceUrl → Bot Service → Teams
        ↓
Saves updated ITurnState to Blob Storage
        ↓
ZavaInsuranceAgent destroyed (was Transient)
```
