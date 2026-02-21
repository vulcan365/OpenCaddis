using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using FabrCore.Core;

namespace OpenCaddis.Services;

public class OpenCaddisConfigurationDto
{
    public List<AgentConfigurationDto> Agents { get; set; } = [];
    public Microsoft365ConfigurationDto? Microsoft365 { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public class Microsoft365ConfigurationDto
{
    public string ClientId { get; set; } = string.Empty;
    public string? EncryptedTokens { get; set; }
    public string? UserDisplayName { get; set; }
    public string? UserEmail { get; set; }
}

public class AgentConfigurationDto
{
    [Required]
    public string Handle { get; set; } = string.Empty;

    [Required]
    public string AgentType { get; set; } = string.Empty;

    public string Models { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public Dictionary<string, string> Args { get; set; } = new();

    public List<string> Plugins { get; set; } = [];

    public List<string> Tools { get; set; } = [];

    public List<McpServerConfig> McpServers { get; set; } = [];
}
