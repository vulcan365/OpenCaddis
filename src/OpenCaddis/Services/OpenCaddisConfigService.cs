using System.Text.Json;

namespace OpenCaddis.Services;

public class OpenCaddisConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _configPath;

    public OpenCaddisConfigService(IWebHostEnvironment environment)
    {
        _configPath = Path.Combine(environment.ContentRootPath, "opencaddis.json");
    }

    public bool ConfigurationExists() => File.Exists(_configPath);

    public async Task<OpenCaddisConfigurationDto> LoadConfigurationAsync()
    {
        var json = await File.ReadAllTextAsync(_configPath);
        return JsonSerializer.Deserialize<OpenCaddisConfigurationDto>(json, SerializerOptions)
               ?? new OpenCaddisConfigurationDto();
    }

    public async Task SaveConfigurationAsync(OpenCaddisConfigurationDto configuration)
    {
        var json = JsonSerializer.Serialize(configuration, SerializerOptions);
        await File.WriteAllTextAsync(_configPath, json);
    }
}
