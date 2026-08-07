using Microsoft.Extensions.DependencyInjection;
using OpenCaddis.Sdk.Connections;

namespace OpenCaddis.Sdk.Addons;

public interface IOpenCaddisAddonModule
{
    void Configure(OpenCaddisAddonBuilder builder);
}

public sealed class OpenCaddisAddonBuilder
{
    private readonly List<ConnectionProviderRegistration> providers = [];
    private readonly List<ConnectionRequirement> requirements = [];
    private string? id;
    private string? displayName;
    private string? version;

    public OpenCaddisAddonBuilder(IServiceCollection services)
    {
        Services = services;
    }

    public IServiceCollection Services { get; }

    public OpenCaddisAddonBuilder SetIdentity(string addonId, string name, string addonVersion)
    {
        id = addonId;
        displayName = name;
        version = addonVersion;
        return this;
    }

    public OpenCaddisAddonBuilder AddConnectionProvider<TProvider>(ConnectionProviderDescriptor descriptor)
        where TProvider : class, IOpenCaddisConnectionProvider
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Services.AddSingleton<TProvider>();
        providers.Add(new ConnectionProviderRegistration(descriptor, typeof(TProvider)));
        return this;
    }

    public OpenCaddisAddonBuilder AddConnectionProvider(ConnectionProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.AuthenticationKind == ConnectionAuthenticationKind.Custom)
        {
            throw new ArgumentException(
                "Custom providers must be registered with AddConnectionProvider<TProvider>.",
                nameof(descriptor));
        }

        providers.Add(new ConnectionProviderRegistration(descriptor, null));
        return this;
    }

    public OpenCaddisAddonBuilder AddConnectionRequirement(ConnectionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        requirements.Add(requirement);
        return this;
    }

    public OpenCaddisAddonRegistration Build()
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("An add-on module must call SetIdentity exactly once.");
        }

        return new OpenCaddisAddonRegistration(id, displayName, version, providers, requirements);
    }
}

public sealed record ConnectionProviderRegistration(
    ConnectionProviderDescriptor Descriptor,
    Type? ImplementationType);

public sealed record OpenCaddisAddonRegistration(
    string Id,
    string DisplayName,
    string Version,
    IReadOnlyList<ConnectionProviderRegistration> Providers,
    IReadOnlyList<ConnectionRequirement> Requirements);
