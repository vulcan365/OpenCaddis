using System.Text.Json;

namespace OpenCaddis.Services;

public class FabrCoreConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _configPath;

    public FabrCoreConfigService(IWebHostEnvironment environment)
    {
        _configPath = Path.Combine(environment.ContentRootPath, "fabrcore.json");
    }

    public bool ConfigurationExists() => File.Exists(_configPath);

    public async Task<FabrCoreConfigurationDto> LoadConfigurationAsync()
    {
        var json = await File.ReadAllTextAsync(_configPath);
        return JsonSerializer.Deserialize<FabrCoreConfigurationDto>(json, SerializerOptions)
               ?? new FabrCoreConfigurationDto();
    }

    public async Task SaveConfigurationAsync(FabrCoreConfigurationDto configuration)
    {
        var json = JsonSerializer.Serialize(configuration, SerializerOptions);
        await File.WriteAllTextAsync(_configPath, json);
    }
}
