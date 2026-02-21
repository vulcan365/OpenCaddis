using System.Text.Json;
using OpenCaddis.Sdk;

namespace OpenCaddis.Services;

public class OpenCaddisConfigService : ICaddisConfigService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
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

    public async Task<T?> GetAddonConfigAsync<T>(string sectionName) where T : class
    {
        if (!ConfigurationExists()) return null;
        var config = await LoadConfigurationAsync();
        if (config.ExtensionData?.TryGetValue(sectionName, out var element) == true)
            return element.Deserialize<T>(SerializerOptions);
        return null;
    }

    public async Task SetAddonConfigAsync<T>(string sectionName, T value) where T : class
    {
        var config = ConfigurationExists()
            ? await LoadConfigurationAsync()
            : new OpenCaddisConfigurationDto();
        config.ExtensionData ??= new();
        config.ExtensionData[sectionName] = JsonSerializer.SerializeToElement(value, SerializerOptions);
        await SaveConfigurationAsync(config);
    }
}
