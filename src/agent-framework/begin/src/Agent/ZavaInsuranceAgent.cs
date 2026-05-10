using InsuranceAgent;
using InsuranceAgent.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;
using Microsoft.Extensions.AI;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ZavaInsurance.Plugins;

namespace ZavaInsurance.Agent
{
    /// <summary>
    /// Zava Insurance Claims Agent - Stage 1: Quick Wins with Microsoft 365 Copilot
    /// 
    /// This agent demonstrates how Zava Insurance can leverage intelligent agents 
    /// to streamline claims processing, reduce manual work, and improve customer satisfaction.
    /// 
    /// Scenario: From claims chaos to an agent-powered operations fabric
    /// - Reduces context switching between Outlook, Teams, SharePoint, and claims systems
    /// - Centralizes access to field photos, invoices, and inspection notes
    /// - Provides real-time visibility into bottlenecks and fraud indicators
    /// </summary>
    /// <remarks>
    /// Questa classe deriva da <see cref="AgentApplication"/> (M365 Agents SDK) e registra
    /// tre handler usando tre meccanismi distinti offerti dal framework:
    ///
    /// 1. <b>OnConversationUpdate</b> (nel costruttore) — registra <c>WelcomeMessageAsync</c>
    ///    come handler per l'evento MembersAdded. Il framework lo chiama automaticamente
    ///    quando un utente entra nella conversazione.
    ///
    /// 2. <b>OnActivity</b> (nel costruttore) — registra <c>OnMessageAsync</c> come catch-all
    ///    per qualsiasi messaggio di testo. DEVE essere registrato per ultimo perché
    ///    AgentApplication valuta le route nell'ordine di registrazione e si ferma al
    ///    primo match.
    ///
    /// 3. <b>[Route] attribute</b> (su <c>Reset</c>) — il costruttore di AgentApplication
    ///    chiama ApplyRouteAttributes() che scansiona via reflection i metodi decorati
    ///    con [Route] e li registra automaticamente, senza codice esplicito nel costruttore.
    ///
    /// I metodi <c>GetClientAgent</c> e <c>GetConversationThread</c> sono helper privati
    /// chiamati esplicitamente da <c>OnMessageAsync</c>, non handler del framework.
    /// Il metodo <c>GetUserProfile</c> è scaffolding preparato per passi successivi.
    /// </remarks>
    public class ZavaInsuranceAgent : AgentApplication
    {
        private readonly string AgentInstructions = """
        You are a professional insurance claims assistant for Zava Insurance that helps adjusters process claims efficiently.

        Whenever the user starts a new conversation or provides a prompt to start a new conversation like "start over", "restart", 
        "new conversation", "what can you do?", "how can you help me?", etc. use {{StartConversationPlugin.StartConversation}} and 
        provide to the user exactly the message you get back from the plugin.

        Use {{DateTimeFunctionTool.getDate}} to get the current date and time.
        
        Stick to the scenario above. If something falls outside of claims insurance context, respond to the user politely with your scope limits.
        """;

        private readonly HttpClient _httpClient = null;
        private readonly IChatClient? _chatClient = null;
        private readonly IConfiguration? _configuration = null;
        private readonly IServiceProvider _serviceProvider;

        public ZavaInsuranceAgent(AgentApplicationOptions options, IChatClient chatClient, IConfiguration configuration, IServiceProvider serviceProvider, IHttpClientFactory httpClientFactory) : base(options)
        {
            _chatClient = chatClient;
            _configuration = configuration;
            _serviceProvider = serviceProvider;
            _httpClient = httpClientFactory.CreateClient() ?? throw new ArgumentNullException(nameof(httpClientFactory));

            // Greet when members are added to the conversation
            // Registra WelcomeMessageAsync come handler per l'evento MembersAdded
            // Quindi è AgentApplication (la base class) che, quando riceve una activity in ingresso dal canale, 
            // scorre internamente la lista di route registrate e chiama automaticamente il tuo handler WelcomeMessageAsync. 
            // Tu non invochi mai WelcomeMessageAsync direttamente — ci pensa il framework.
            OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeMessageAsync);

            // Listen for ANY message to be received. MUST BE AFTER ANY OTHER MESSAGE HANDLERS
            // Stesso meccanismo di registrazione, ma per un tipo di activity diverso (Message) e un handler diverso (OnMessageAsync).
            // Registra OnMessageAsync come handler per qualsiasi activity di tipo message — cioè ogni volta che l'utente invia un testo nella chat.

            // C'è però un dettaglio importante che il commento sottolinea: "MUST BE AFTER ANY OTHER MESSAGE HANDLERS".
            // Questo perché AgentApplication valuta le route nell'ordine in cui sono state registrate e si ferma alla prima che fa match.
            // Registrandolo per ultimo, si comporta come un catch-all — gestisce tutti i messaggi che non sono stati 
            // già intercettati da handler più specifici (come ad esempio un handler per un comando specifico o per un pattern di testo).
            OnActivity(ActivityTypes.Message, OnMessageAsync);
        }

        protected async Task WelcomeMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
        {
            var startConversation = new StartConversationPlugin();
            var welcomeMessage = await startConversation.StartConversation();

            foreach (ChannelAccount member in turnContext.Activity.MembersAdded)
            {
                if (member.Id != turnContext.Activity.Recipient.Id)
                {
                    await turnContext.StreamingResponse.QueueInformativeUpdateAsync(welcomeMessage, cancellationToken);
                }
            }
        }

        protected async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
        {
            // Start a Streaming Process to let clients that support streaming know that we are processing the request. 
            await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Processing your request...", cancellationToken).ConfigureAwait(false);

            try
            {
                var userText = turnContext.Activity.Text?.Trim() ?? string.Empty;
                var _agent = GetClientAgent(turnContext, turnState);

                // Read or Create the conversation thread for this conversation.
                AgentThread? thread = GetConversationThread(_agent, turnState);

                // Stream the response back to the user as we receive it from the agent.
                await foreach (var response in _agent.RunStreamingAsync(userText, thread, cancellationToken: cancellationToken))
                {
                    // Log out tool calls for monitoring and compliance
                    if (response.Role == ChatRole.Tool)
                    {
                        System.Diagnostics.Trace.WriteLine($"Tool called with response: {response.Text}");
                    }

                    if (response.Role == ChatRole.Assistant && !string.IsNullOrEmpty(response.Text))
                    {
                        turnContext.StreamingResponse.QueueTextChunk(response.Text);
                    }
                }

                // Save the updated thread state back to the conversation state.
                turnState.Conversation.SetValue("conversation.threadInfo", ProtocolJsonSerializer.ToJson(thread.Serialize()));
            }
            finally
            {
                await turnContext.StreamingResponse.EndStreamAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Resolve the ChatClientAgent with tools and options for this turn operation. 
        /// This will use the IChatClient registered in DI.
        /// </summary>
        private ChatClientAgent GetClientAgent(ITurnContext context, ITurnState turnState)
        {
            AssertionHelpers.ThrowIfNull(_configuration!, nameof(_configuration));
            AssertionHelpers.ThrowIfNull(context, nameof(context));
            AssertionHelpers.ThrowIfNull(_chatClient!, nameof(_chatClient));

            // Setup the plugins with access to the Agent SDK current context and services
            StartConversationPlugin startConversationPlugin = new();

            // Setup the tools for the agent using Agent Framework
            var toolOptions = new ChatOptions
            {
                Temperature = (float?)1,
                Tools = new List<AITool>()
            };

            // Add Start Conversation tool
            toolOptions.Tools.Add(AIFunctionFactory.Create(startConversationPlugin.StartConversation));

            // Add DateTime tool
            toolOptions.Tools.Add(AIFunctionFactory.Create(DateTimeFunctionTool.getDate));

            // Create the chat Client passing in agent instructions and tools
            return new ChatClientAgent(_chatClient!,
                    new ChatClientAgentOptions
                    {
                        Name = "Zava Insurance Claims Agent",
                        Instructions = AgentInstructions,
                        ChatOptions = toolOptions,
                        ChatMessageStoreFactory = ctx =>
                        {
#pragma warning disable MEAI001 // MessageCountingChatReducer is for evaluation purposes only and is subject to change or removal in future updates
                            return new InMemoryChatMessageStore(new MessageCountingChatReducer(15), ctx.SerializedState, ctx.JsonSerializerOptions);
#pragma warning restore MEAI001 // MessageCountingChatReducer is for evaluation purposes only and is subject to change or removal in future updates
                        }
                    });
        }

        /// <summary>
        /// Manage Agent threads against the conversation state.
        /// </summary>
        private AgentThread GetConversationThread(ChatClientAgent agent, ITurnState turnState)
        {
            AgentThread thread;
            string? agentThreadInfo = turnState.Conversation.GetValue<string?>("conversation.threadInfo", () => null);
            if (string.IsNullOrEmpty(agentThreadInfo))
            {
                thread = agent.GetNewThread();
            }
            else
            {
                JsonElement ele = ProtocolJsonSerializer.ToObject<JsonElement>(agentThreadInfo);
                thread = agent.DeserializeThread(ele);
            }
            return thread;
        }

        [Route(RouteType = RouteType.Message, Type = ActivityTypes.Message, Text = "-reset")]
        protected async Task Reset(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
        {
            await UserAuthorization.SignOutUserAsync(turnContext, turnState, "me", cancellationToken: cancellationToken);
            turnState.Conversation.ClearConversationHistory();
            turnState.Conversation.ClearCachedUserProfile();
            await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Reset complete", cancellationToken: cancellationToken);
        }

        private async Task<UserProfile> GetUserProfile(string accessToken, CancellationToken cancellationToken)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            HttpResponseMessage response = await _httpClient.GetAsync("https://graph.microsoft.com/v1.0/me?$select=department,jobTitle,preferredLanguage,displayName,givenName,companyName,userPrincipalName,id,mail", cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<UserProfile>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            })!;
        }
    }
}
