using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Grok;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Plugins.Web;
using Microsoft.SemanticKernel.Plugins.Web.Google;
using MultiAgentThinkGroup;
using System.Windows;
using System.Windows.Threading;

namespace MultiAgentWPF  // Update to your namespace
{
    public partial class MainWindow : Window
    {
        //private MultiAgentThinkOrchestrator _orchestrator = new MultiAgentThinkOrchestrator();
        private List<Kernel> _kernels = new List<Kernel>();
        private ChatHistory _sharedHistory = new ChatHistory();

        private static readonly string openAIKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY not set.");
        private static readonly string googleGeminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? throw new InvalidOperationException("GEMINI_API_KEY not set.");
        private static readonly string googleCustomSearchKey = Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? throw new InvalidOperationException("GOOGLE_CUSTOM_SEARCH_API_KEY not set.");
        private static readonly string googleSearchEngineId = Environment.GetEnvironmentVariable("GOOGLE_SEARCH_ENGINE_ID") ?? throw new InvalidOperationException("GOOGLE_SEARCH_ENGINE_ID not set.");
        private static readonly string grokKey = Environment.GetEnvironmentVariable("GROK_API_KEY") ?? throw new InvalidOperationException("GROK_API_KEY not set.");

        private static readonly string openAIModel = "gpt-5.1";
        private static readonly string googleModel = "gemini-3-pro-preview";
        private static readonly string grokModel = "grok-4-1-fast-non-reasoning";

        private static GoogleConnector googleConnector = new GoogleConnector(apiKey: googleCustomSearchKey, searchEngineId: googleSearchEngineId);

        public MainWindow()
        {
            InitializeComponent();
            InitializeKernelsAsync();  // No await needed here
        }

        private async void InitializeKernelsAsync()
        {
            try
            {
                Algos.AddConsoleLogger("MultiAgentThinkGroupLog.txt");

                var orchestrator = new MultiAgentThinkOrchestrator();

                var grokBuilder = Kernel.CreateBuilder().AddGrokChatCompletion(grokModel, grokKey);
                //grokBuilder.Services.AddSingleton<IChatCompletionService>(new GrokChatCompletionService(grokKey, grokModel));
                grokBuilder.Services.Configure<OpenAIPromptExecutionSettings>(s => s.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions);

                var chatGPTBuilder = Kernel.CreateBuilder().AddOpenAIChatCompletion(openAIModel, openAIKey);
                chatGPTBuilder.Services.Configure<OpenAIPromptExecutionSettings>(s => s.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions);

                var geminiBuilder = Kernel.CreateBuilder().AddGoogleAIGeminiChatCompletion(googleModel, googleGeminiKey);
                geminiBuilder.Services.Configure<OpenAIPromptExecutionSettings>(s => s.ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions);

                var geminiKernel = geminiBuilder.Build();
                var grokKernel = grokBuilder.Build();
                var chatGPTKernel = chatGPTBuilder.Build();


                // Create a web search engine plugin, add to kernels.
                var google = new WebSearchEnginePlugin(googleConnector);

                grokKernel.Plugins.AddFromObject(google, "google");
                chatGPTKernel.Plugins.AddFromObject(google, "google");
                geminiKernel.Plugins.AddFromObject(google, "google");

                // Also add structured output plugin
                //var structuredPlugin = KernelPluginFactory.CreateFromType<StructuredOutputPlugin>();
                //grokKernel.Plugins.Add(structuredPlugin);
                //chatGPTKernel.Plugins.Add(structuredPlugin);
                //geminiKernel.Plugins.Add(structuredPlugin);

                _kernels = new List<Kernel> { grokKernel, chatGPTKernel, geminiKernel };  // First = judge
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => FinalAnswer.Text = $"Init Error: {ex.Message}");
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            var query = UserInput.Text?.Trim();
            if (string.IsNullOrEmpty(query)) return;

            UserInput.Text = "";
            FinalAnswer.Text = "Agents thinking...";

            _sharedHistory.AddUserMessage(query);

            // TODO

            //await _orchestrator.RunWithLiveUIAsync(
            //    query,
            //    _kernels,
            //    update =>
            //    {
            //        Dispatcher.Invoke(() =>
            //        {
            //            GrokThought.Text = update.Grok.Thought;
            //            GrokAction.Text = string.IsNullOrEmpty(update.Grok.Action) ? "" : $"Action: {update.Grok.Action}";
            //            GrokObservation.Text = string.IsNullOrEmpty(update.Grok.Observation) ? "" : $"Observation: {update.Grok.Observation}";

            //            GptThought.Text = update.Gpt.Thought;
            //            GptAction.Text = string.IsNullOrEmpty(update.Gpt.Action) ? "" : $"Action: {update.Gpt.Action}";
            //            GptObservation.Text = string.IsNullOrEmpty(update.Gpt.Observation) ? "" : $"Observation: {update.Gpt.Observation}";

            //            GeminiThought.Text = update.Gemini.Thought;

            //            if (update.Final != null)
            //            {
            //                FinalConfidence.Text = $"Confidence: {update.Final.Confidence:P1}";
            //                FinalAnswer.Text = update.Final.FinalAnswer;
            //                if (update.Final.Sources?.Any() == true)
            //                    FinalAnswer.Text += $"\n\nSources: {string.Join(", ", update.Final.Sources)}";
            //            }
            //        });
            //    });
        }
    }
}