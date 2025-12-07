using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Serilog;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiAgentThinkGroup;


/// <summary>
/// Upgraded LlmChat class using Semantic Kernel for enhanced tool calling.
/// Integrates profile configs for dynamic prompts from JSON.
/// Tools loaded dynamically via kernel factory; assumes pre-loaded kernel.
/// TODO: Add rate-limiting for API usage, it should not spam queries and rack up costs.
/// </summary>
public class LlmChat
{
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chatService;
    private readonly ChatHistory _chatHistory;
    private readonly string _systemPrompt;
    private readonly float _temperature;
    private readonly int _maxTokens;
    private readonly ImmutableList<KernelFunctionMetadata> _functionsMetadata;

    /// <summary>
    /// Construct an LLM chat instance with tool calling via Semantic Kernel.
    /// </summary>
    /// <param name="config"></param>
    /// <param name="toolFunctions"></param>
    /// <param name="kernel"></param>
    public LlmChat(
        (float? Temperature, int MaxTokens, string InstructionsText) config,
        StructuredOutputPlugin toolFunctions,
        Kernel kernel) // Required pre-loaded kernel
    {
        ArgumentNullException.ThrowIfNull(toolFunctions);
        ArgumentNullException.ThrowIfNull(kernel);

        _kernel = kernel;
        _kernel.ImportPluginFromObject(toolFunctions, pluginName: "SemanticTools");

        _temperature = config.Temperature ?? 0.7f;
        _maxTokens = config.MaxTokens > 0 ? config.MaxTokens : 1024; // Robust default

        _chatService = _kernel.GetRequiredService<IChatCompletionService>();

        // Validate tools/plugins loaded (robust: Use GetFunctionsMetadata for count + metadata)
        _functionsMetadata = _kernel.Plugins.GetFunctionsMetadata().ToImmutableList();
        var pluginCount = _kernel.Plugins.Count;
        var functionCount = _functionsMetadata.Count;
        Log.Information("Kernel loaded with {PluginCount} plugins and {FunctionCount} functions.", pluginCount, functionCount);

        // Log metadata for debug
        if (functionCount > 0)
        {
            var sampleFunc = _functionsMetadata[0];
            var paramCount = sampleFunc.Parameters.Count;
            Log.Debug("Sample function '{Name}': {ParamCount} params (e.g., {FirstParam})",
                sampleFunc.Name, paramCount, sampleFunc.Parameters.FirstOrDefault()?.Name ?? "none");
        }
        else
        {
            Log.Warning("No tools/functions registered—check plugin loader/DLL. Falling back to prompt-only chat.");
        }

        // Quick 422 risk check (modern: Flag non-string req'd params)
        var riskyParams = _functionsMetadata
            .SelectMany(f => f.Parameters.Where(p => p.IsRequired && p.ParameterType != typeof(string)))
            .ToList();
        if (riskyParams.Any())
        {
            Log.Warning("Potential 422 risks: {RiskyCount} non-string required params across tools (xAI strict). Consider stringifying params.", riskyParams.Count);
        }

        // Build dynamic system prompt from profile (now uses loaded metadata)
        _systemPrompt = BuildSystemPrompt(config.InstructionsText, _functionsMetadata);

        // Chat history for multi-turn context
        _chatHistory = new ChatHistory();
    }

    /// <summary>
    /// Processes a user message using function calling for intelligent tool invocation.
    /// With AutoInvoke, a single call handles tool execution and returns the final response.
    /// Disables tool calling if no functions loaded (robust against 422 errors).
    /// </summary>
    public async Task<string> ProcessUserQueryAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(userMessage);

        if (_chatHistory.Count == 0)
        {
            _chatHistory.AddSystemMessage(_systemPrompt);
        }

        _chatHistory.AddUserMessage(userMessage);

        var executionSettings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = _functionsMetadata.Count > 0 ? ToolCallBehavior.AutoInvokeKernelFunctions : null,
            Temperature = _temperature,
            MaxTokens = _maxTokens
        };

        try
        {
            var response = await _chatService.GetChatMessageContentAsync(
                _chatHistory,
                executionSettings,
                _kernel,
                cancellationToken);

            _chatHistory.Add(response);

            return response?.Content ?? "No response generated.";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "LLM processing failed for message: {Message}", userMessage);
            return $"Error in processing: {ex.Message}. Falling back to basic chat.";
        }
    }

    /// <summary>
    /// Builds the full system prompt from profile config (Instructions + Addendum + interpolated ToolGuidance).
    /// Enhanced: Dynamically includes descriptions/params only for loaded tools (avoids mismatch if none loaded).
    /// </summary>
    private static string BuildSystemPrompt(string instructionsText, ImmutableList<KernelFunctionMetadata> functionsMetadata)
    {
        var promptBuilder = new StringBuilder(instructionsText ?? string.Empty);

        // Enhance with loaded tool descriptions/params (immutable; detailed for better LLM guidance)
        if (functionsMetadata != null && functionsMetadata.Count > 0)
        {
            promptBuilder.AppendLine();
            promptBuilder.AppendLine();
            promptBuilder.AppendLine("You have access to the following tools. Use them when helpful:");
            promptBuilder.AppendLine();

            foreach (var f in functionsMetadata)
            {
                promptBuilder.AppendLine($"Tool: {f.Name}");
                if (!string.IsNullOrWhiteSpace(f.PluginName))
                {
                    promptBuilder.AppendLine($"  Plugin: {f.PluginName}");
                }

                if (!string.IsNullOrWhiteSpace(f.Description))
                {
                    promptBuilder.AppendLine($"  Description: {f.Description}");
                }

                if (f.Parameters.Any())
                {
                    promptBuilder.AppendLine("  Parameters:");
                    foreach (var p in f.Parameters)
                    {
                        promptBuilder.AppendLine(
                            $"    - {p.Name} ({p.ParameterType?.Name ?? "string"}): " +
                            $"{(string.IsNullOrWhiteSpace(p.Description) ? "No description" : p.Description)} " +
                            $"(required: {p.IsRequired}, default: {p.DefaultValue ?? "none"})");
                    }
                }

                promptBuilder.AppendLine();
            }

            // Optional extra nudge for the structured output flow
            promptBuilder.AppendLine(
                "When you are ready to provide your final answer, call the tool " +
                "`submit_structured_response` exactly once with the fully-populated JSON.");

            promptBuilder.AppendLine();
            promptBuilder.AppendLine("YOU MUST FOLLOW THESE RULES:");
            promptBuilder.AppendLine("- Your entire response MUST be a single function call.");
            promptBuilder.AppendLine("- Do NOT output any text outside the function call.");
            promptBuilder.AppendLine("- Do NOT output markdown, explanations, tags, XML/HTML, or extra JSON.");
            promptBuilder.AppendLine("- The JSON inside the function call must be valid and complete.");
            promptBuilder.AppendLine("If you produce ANYTHING outside the function call, you are violating the rules.");


        }

        return promptBuilder.ToString();
    }


    public void ClearHistory()
    {
        _chatHistory.Clear();
        _chatHistory.AddSystemMessage(_systemPrompt);  // Ensure system prompt is always present after clear
    }

    public void AddAssistantMessage(string message)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(message);
        _chatHistory.AddAssistantMessage(message);
    }
}