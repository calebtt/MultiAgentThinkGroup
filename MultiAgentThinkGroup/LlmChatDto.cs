using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace MultiAgentThinkGroup;

/// <summary>
/// Records for JSON tool schema (OpenAPI-style for SK compatibility). Immutable by design.
/// </summary>
public record ToolSchema
{
    [JsonPropertyName("name")]
    public string Name { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; }

    [JsonPropertyName("parameters")]
    public ParametersSchema Parameters { get; init; }

    public ToolSchema(string name, string description, ParametersSchema parameters)
    {
        Name = name;
        Description = description;
        Parameters = parameters;
    }

    public record ParametersSchema(
        [property: JsonPropertyName("type")] string Type, // e.g., "object"
        [property: JsonPropertyName("properties")] IReadOnlyDictionary<string, ParamSchema> Properties,
        [property: JsonPropertyName("required")] string[]? Required = null);

    public record ParamSchema(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("default")] object? Default = null,
        [property: JsonPropertyName("enum")] string[]? Enum = null);
}

/// <summary>
/// Immutable record for per-tool guidance. Supports priority for prompt ordering.
/// </summary>
public record ToolInstruction(
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("guidance")] string Guidance,
    [property: JsonPropertyName("priority")] int Priority = 0);

/// <summary>
/// Enum for prompt section ordering. Extensible for future types.
/// </summary>
public enum SectionType
{
    Base = 0,
    Addendum = 1,
    Rules = 2,
    Tools = 3  // Placeholder for tool-specific insertion
}

/// <summary>
/// Immutable record for composable prompt sections.
/// </summary>
public record PromptSection(
    [property: JsonPropertyName("type")] SectionType Type,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("condition")] string? Condition = null);  // e.g., "tools_loaded"

/// <summary>
/// Immutable record for rules (extracted from ToolGuidance).
/// </summary>
public record Rule(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("appliesTo")] string[] AppliesTo = null!);  // Default to global if empty

/// <summary>
/// Immutable record for LLM configuration. Loaded from JSON; supports modular prompt/rules.
/// Legacy fields (InstructionsAddendum, ToolGuidance) deserialized but ignored.
/// </summary>
public record LanguageModelConfig(
    [property: JsonPropertyName("ApiKeyEnvironmentVariable")] string ApiKeyEnvironmentVariable = "",
    [property: JsonPropertyName("Model")] string Model = "",
    [property: JsonPropertyName("EndPoint")] string EndPoint = "",
    [property: JsonPropertyName("MaxTokens")] int MaxTokens = 0,
    [property: JsonPropertyName("WelcomeMessage")] string WelcomeMessage = "",
    [property: JsonPropertyName("WelcomeFilePath")] string WelcomeFilePath = "recordings/welcome_message.wav",
    [property: JsonPropertyName("InstructionsText")] string InstructionsText = "",
    [property: JsonPropertyName("Temperature")] float? Temperature = 0.1f,
    [property: JsonPropertyName("Tools")] List<ToolSchema> Tools = default!,
    [property: JsonPropertyName("ToolInstructions")] List<ToolInstruction>? ToolInstructions = default,
    [property: JsonPropertyName("PromptSections")] List<PromptSection>? PromptSections = default,
    [property: JsonPropertyName("Rules")] List<Rule>? Rules = default);
