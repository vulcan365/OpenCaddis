using Microsoft.Extensions.DependencyInjection;

namespace OpenCaddis.Sdk;

public interface ICaddisAddon
{
    string Name { get; }
    void ConfigureServices(IServiceCollection services);
    Type? SettingsComponentType => null;
}
