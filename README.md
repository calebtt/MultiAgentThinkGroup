# Multi-Agent Think Group (MATG)

A multi-model AI reasoning system that orchestrates structured debates between frontier LLMs to produce higher-quality, more reliable answers.

## Overview

MATG sends a query to multiple AI models (Grok, ChatGPT, Gemini), has them generate structured reasoning chains, then facilitates a panel discussion where each model critiques the others' reasoning. A judge model synthesizes the strongest arguments into a final merged response.

The core insight: **different models have different blind spots**. By making them debate and defend their reasoning, we surface assumptions, identify risks, and produce answers that are more robust than any single model alone.

## How It Works

```
┌─────────────────────────────────────────────────────────────────────┐
│                         User Query                                  │
└─────────────────────────────────────────────────────────────────────┘
                                 │
                                 ▼
         ┌───────────────────────┼───────────────────────┐
         │                       │                       │
         ▼                       ▼                       ▼
    ┌─────────┐            ┌─────────┐            ┌─────────┐
    │  Grok   │            │ ChatGPT │            │ Gemini  │
    │         │            │         │            │         │
    │ Initial │            │ Initial │            │ Initial │
    │Reasoning│            │Reasoning│            │Reasoning│
    └────┬────┘            └────┬────┘            └────┬────┘
         │                      │                      │
         └──────────────────────┼──────────────────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │   Panel Discussion    │
                    │   (N rounds)          │
                    │                       │
                    │ Each agent critiques  │
                    │ others' reasoning     │
                    └───────────┬───────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │      Judge Agent      │
                    │                       │
                    │ Merges best reasoning │
                    │ into final response   │
                    └───────────┬───────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │   Merged Response     │
                    │   with confidence     │
                    └───────────────────────┘
```

## Features

- **Structured Reasoning**: All models output tagged reasoning steps (`[PROBLEM]`, `[EVIDENCE]`, `[RISK]`, `[INFERENCE]`, etc.) for transparent chain-of-thought
- **Multi-Model Panel**: Grok, ChatGPT, and Gemini debate each other's conclusions
- **Self-Calibrating Confidence**: Models adjust confidence scores through discussion
- **Custom Grok Connector**: Includes a Semantic Kernel connector for xAI's Grok API with structured output support
- **Reasoning Analytics**: Parse and analyze reasoning patterns across models

## Project Structure

```
MultiAgentThinkGroup/
├── Program.cs                      # Entry point and orchestration
├── MultiAgentThinkOrchestrator.cs  # Core agent invocation logic
├── DialogueMergeSingleJudge.cs     # Panel discussion + judge pipeline
├── AgentConvoClient.cs             # Multi-agent conversation engine
├── ZeroShotSingleJudge.cs          # Single-judge merge (no dialogue)
├── StructuredResponse.cs           # Response schema + JSON helpers
├── Prompts.cs                       # System prompts for all agents
├── ReasoningAnalysis.cs            # Reasoning step parser + stats
├── Algos.cs                        # Agent factories + utilities
├── Types.cs                        # Core types (PanelMessage, etc.)
│
└── Grok Connector/
    ├── GrokChatCompletionService.cs
    ├── GrokChatCompletionClient.cs
    ├── GrokClientBase.cs
    └── GrokConfigTypes.cs
```

## Getting Started

### Prerequisites

- .NET 8.0 or later
- API keys for the models you want to use

### Environment Variables

```bash
export OPENAI_API_KEY="sk-..."
export GEMINI_API_KEY="..."
export GROK_API_KEY="xai-..."

# Optional: for web search plugin
export GOOGLE_API_KEY="..."
export GOOGLE_SEARCH_ENGINE_ID="..."
```

### Installation

```bash
git clone https://github.com/yourusername/MultiAgentThinkGroup.git
cd MultiAgentThinkGroup
dotnet restore
dotnet run
```

## Usage

### Basic Multi-Agent Query

```csharp
// Create kernels for each model
var grokKernel = Kernel.CreateBuilder()
    .AddGrokChatCompletion("grok-4-1-fast-reasoning", grokKey)
    .Build();
var chatGPTKernel = Kernel.CreateBuilder()
    .AddOpenAIChatCompletion("gpt-4o", openAIKey)
    .Build();
var geminiKernel = Kernel.CreateBuilder()
    .AddGoogleAIGeminiChatCompletion("gemini-1.5-pro", geminiKey)
    .Build();

// Get initial structured responses from each model
var grokInitial = await MultiAgentThinkOrchestrator
    .InvokeForStructuredResponseAsync(
        Algos.CreateGrokAgent(grokKernel, Prompts.InitialStepPrompt), 
        query);

// ... same for other models

// Set up the panel and judge
var panelAgents = new List<PanelAgentDescriptor>
{
    new("Grok", grokKernel),
    new("ChatGPT", chatGPTKernel),
    new("Gemini", geminiKernel)
};

var judge = new ZeroShotSingleJudge(Algos.CreateGrokJudgeAgent(grokKernel));
var orchestrator = new DialogueMergeSingleJudge(panelAgents, judge);

// Run 2 rounds of discussion, then merge
var (transcript, merged) = await orchestrator.RunAsync(
    query, 
    initialResponses, 
    rounds: 2);

Console.WriteLine(merged.FinalAnswer);
Console.WriteLine($"Confidence: {merged.Confidence}");
```

### Structured Response Format

All models return responses in this schema:

```json
{
  "reasoning": [
    "[PROBLEM] The user is asking about...",
    "[DECOMPOSITION] Break this into: (1)..., (2)...",
    "[EVIDENCE] According to...",
    "[RISK] This approach has limitations...",
    "[RECOMMENDATION] Based on the above..."
  ],
  "final_answer": "Concise answer for the user...",
  "confidence": 0.85,
  "sources": ["Paper X (2023)", "Documentation Y"]
}
```

### Reasoning Tags

| Tag | Purpose |
|-----|---------|
| `[PROBLEM]` | Clarifying scope, interpreting the question |
| `[CONTEXT]` | Background facts, definitions, constraints |
| `[ASSUMPTION]` | Explicit assumptions being made |
| `[DECOMPOSITION]` | Breaking into subproblems |
| `[EVIDENCE]` | Facts, citations, calculations |
| `[INFERENCE]` | Logical conclusions from evidence |
| `[DECISION]` | Choosing between approaches |
| `[RISK]` | Limitations, failure modes, tradeoffs |
| `[UNCERTAINTY]` | Knowledge gaps, unclear outcomes |
| `[RECOMMENDATION]` | Final internal conclusion |

## Experiment Modes

1. **Initial Structured Reasoning**: Each model independently reasons about the query
2. **Zero-Shot Judge Merge**: Skip discussion, directly merge initial responses
3. **Panel Discussion Only**: Generate transcript without final merge
4. **Full Pipeline**: Discussion rounds → Judge merge (recommended)

## Custom Grok Connector

This project includes a custom Semantic Kernel connector for xAI's Grok API, since the official SK doesn't include one. Features:

- Structured output via `xai_structured_output_mode: "json_schema"`
- Streaming support (SSE)
- Manual function-calling loop for tool use
- Compatible with SK's `ChatCompletionAgent`

See `GrokChatCompletionService.cs` for implementation details.

## Example Output

```
Agent: Grok | Round: 1
- **Agreements**: All three responses correctly prioritize scaling laws, 
  transformers, alignment/RLHF...
- **ChatGPT weaknesses**: Overly broad decomposition into 10+ pillars...
- **Gemini strengths/weaknesses**: Strong evidence chain but incomplete 
  risk coverage...

Agent: ChatGPT | Round: 1
- I largely agree on the technical core. However, both of you underplay 
  evaluation and robustness as standalone research focuses...

=== Final Merged Response ===
Confidence: 0.92
Important points in modern AI research include these 7 key pillars:
1. Foundation Models & Scaling Laws...
2. Training Paradigms...
...
```

## Known Limitations

- **No real CoT**: Uses simulated chain-of-thought via prompting, not models' native reasoning traces (e.g., o1's hidden CoT)
- **Latency**: Full pipeline with 2 rounds = 9+ sequential API calls (~30-60 seconds)
- **Tool calling**: The Grok connector's auto-invoke requires manual loop handling
- **Cost**: Running 3 frontier models adds up quickly

## Roadmap

- [ ] Integrate native chain-of-thought from models that expose it
- [ ] Parallel agent execution within rounds
- [ ] Streaming output during discussion
- [ ] Web UI for interactive sessions
- [ ] Configurable model selection
- [ ] Cost tracking and budgets

## Contributing

Contributions welcome! Please open an issue first to discuss proposed changes.

## License

MIT License - see [LICENSE](LICENSE) for details.

## Acknowledgments

- Built on [Microsoft Semantic Kernel](https://github.com/microsoft/semantic-kernel)
- Inspired by debate-based AI alignment research
