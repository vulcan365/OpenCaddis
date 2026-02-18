using System.Text.Json;

namespace OpenCaddis.Services;

public class FabrConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _configPath;

    public FabrConfigService(IWebHostEnvironment environment)
    {
        _configPath = Path.Combine(environment.ContentRootPath, "fabr.json");
    }

    public bool ConfigurationExists() => File.Exists(_configPath);

    public async Task<FabrConfigurationDto> LoadConfigurationAsync()
    {
        var json = await File.ReadAllTextAsync(_configPath);
        return JsonSerializer.Deserialize<FabrConfigurationDto>(json, SerializerOptions)
               ?? new FabrConfigurationDto();
    }

    public async Task SaveConfigurationAsync(FabrConfigurationDto configuration)
    {
        var json = JsonSerializer.Serialize(configuration, SerializerOptions);
        await File.WriteAllTextAsync(_configPath, json);
    }
}
