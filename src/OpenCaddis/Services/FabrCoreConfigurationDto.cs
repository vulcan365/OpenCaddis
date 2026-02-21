using System.ComponentModel.DataAnnotations;

namespace OpenCaddis.Services;

public class FabrCoreConfigurationDto
{
    public List<ModelConfigurationDto> ModelConfigurations { get; set; } = [];
    public List<ApiKeyConfigurationDto> ApiKeys { get; set; } = [];
}

public class ModelConfigurationDto
{
    [Required]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string Provider { get; set; } = string.Empty;

    [Required]
    public string Uri { get; set; } = string.Empty;

    [Required]
    public string Model { get; set; } = string.Empty;

    [Required]
    public string ApiKeyAlias { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 60;

    public int? MaxOutputTokens { get; set; }

    public int? ContextWindowTokens { get; set; }
}

public class ApiKeyConfigurationDto
{
    [Required]
    public string Alias { get; set; } = string.Empty;

    [Required]
    public string Value { get; set; } = string.Empty;
}
