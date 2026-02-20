namespace OpenCaddis.Sdk;

public interface ICaddisConfigService
{
    bool ConfigurationExists();
    Task<T?> GetAddonConfigAsync<T>(string sectionName) where T : class;
    Task SetAddonConfigAsync<T>(string sectionName, T config) where T : class;
}
