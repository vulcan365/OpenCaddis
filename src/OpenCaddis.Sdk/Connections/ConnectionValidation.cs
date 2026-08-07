using System.Text.RegularExpressions;
using OpenCaddis.Sdk.Addons;

namespace OpenCaddis.Sdk.Connections;

public static partial class ConnectionValidation
{
    public static void Validate(OpenCaddisAddonRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateId(registration.Id, nameof(registration.Id));

        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in registration.Providers)
        {
            Validate(provider.Descriptor);
            if (!providerIds.Add(provider.Descriptor.Id))
            {
                throw new InvalidOperationException(
                    $"Add-on '{registration.Id}' registers provider '{provider.Descriptor.Id}' more than once.");
            }
        }

        var requirementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in registration.Requirements)
        {
            Validate(requirement);
            if (!string.Equals(requirement.AddonId, registration.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Requirement '{requirement.Id}' belongs to '{requirement.AddonId}', not '{registration.Id}'.");
            }

            if (!requirementIds.Add(requirement.Id))
            {
                throw new InvalidOperationException(
                    $"Add-on '{registration.Id}' registers requirement '{requirement.Id}' more than once.");
            }
        }
    }

    public static void Validate(ConnectionProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateId(descriptor.Id, nameof(descriptor.Id));
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.DisplayName);
        if (!Enum.IsDefined(descriptor.AuthenticationKind))
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' uses unsupported authentication kind '{descriptor.AuthenticationKind}'.");
        }

        if (descriptor.AuthenticationKind is ConnectionAuthenticationKind.OAuthAuthorizationCodePkce &&
            (descriptor.AuthorizationEndpoint is null || descriptor.TokenEndpoint is null))
        {
            throw new InvalidOperationException(
                $"OAuth provider '{descriptor.Id}' requires authorization and token endpoints.");
        }

        if (descriptor.AuthenticationKind is ConnectionAuthenticationKind.OAuthClientCredentials &&
            descriptor.TokenEndpoint is null)
        {
            throw new InvalidOperationException(
                $"Client-credentials provider '{descriptor.Id}' requires a token endpoint.");
        }

        if (descriptor.AuthenticationKind is ConnectionAuthenticationKind.ApiKey &&
            string.IsNullOrWhiteSpace(descriptor.ApiKeyHeaderName))
        {
            throw new InvalidOperationException(
                $"API-key provider '{descriptor.Id}' requires ApiKeyHeaderName.");
        }

        foreach (var endpoint in new[]
                 {
                     descriptor.Authority,
                     descriptor.AuthorizationEndpoint,
                     descriptor.TokenEndpoint,
                     descriptor.RevocationEndpoint,
                     descriptor.UserInfoEndpoint
                 }.Where(endpoint => endpoint is not null).Cast<Uri>())
        {
            if (!endpoint.IsAbsoluteUri ||
                (endpoint.Scheme != Uri.UriSchemeHttps &&
                 !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
            {
                throw new InvalidOperationException(
                    $"Provider '{descriptor.Id}' endpoints must use HTTPS, or HTTP on loopback for local development.");
            }
        }

        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in descriptor.ConfigurationFields)
        {
            ValidateId(field.Name, $"{descriptor.Id}.ConfigurationFields.Name");
            ArgumentException.ThrowIfNullOrWhiteSpace(field.Label);
            if (!fields.Add(field.Name))
            {
                throw new InvalidOperationException(
                    $"Provider '{descriptor.Id}' declares configuration field '{field.Name}' more than once.");
            }
        }

        if (descriptor.DefaultScopes.Any(scope =>
                string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsWhiteSpace)) ||
            descriptor.DefaultScopes.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            descriptor.DefaultScopes.Count)
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' contains malformed or duplicate default scopes.");
        }

        if (descriptor.AuthenticationKind is ConnectionAuthenticationKind.OAuthAuthorizationCodePkce &&
            !fields.Contains(descriptor.ClientIdConfigurationField))
        {
            throw new InvalidOperationException(
                $"Delegated OAuth provider '{descriptor.Id}' must declare its client ID field.");
        }


        if (descriptor.AuthenticationKind == ConnectionAuthenticationKind.ApiKey &&
            (!fields.Contains(descriptor.ApiKeyConfigurationField) ||
             !descriptor.ConfigurationFields.Any(field =>
                 string.Equals(field.Name, descriptor.ApiKeyConfigurationField, StringComparison.OrdinalIgnoreCase) &&
                 (field.Sensitive || field.Kind == ConnectionConfigurationFieldKind.Secret))))
        {
            throw new InvalidOperationException(
                $"API-key provider '{descriptor.Id}' must declare its API-key field as sensitive.");
        }

        if (descriptor.AuthenticationKind is ConnectionAuthenticationKind.OAuthClientCredentials &&
            (!fields.Contains(descriptor.ClientIdConfigurationField) ||
             !descriptor.ConfigurationFields.Any(field =>
                 string.Equals(field.Name, descriptor.ClientSecretConfigurationField, StringComparison.OrdinalIgnoreCase) &&
                 (field.Sensitive || field.Kind == ConnectionConfigurationFieldKind.Secret))))
        {
            throw new InvalidOperationException(
                $"Client-credentials provider '{descriptor.Id}' must declare its client ID and a sensitive client secret.");
        }

        var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in descriptor.Actions)
        {
            ValidateId(action.Id, $"{descriptor.Id}.Actions.Id");
            ArgumentException.ThrowIfNullOrWhiteSpace(action.Label);
            if (!actionIds.Add(action.Id))
            {
                throw new InvalidOperationException(
                    $"Provider '{descriptor.Id}' declares action '{action.Id}' more than once.");
            }
        }
    }

    public static void Validate(ConnectionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        ValidateId(requirement.Id, nameof(requirement.Id));
        ValidateId(requirement.AddonId, nameof(requirement.AddonId));
        ValidateId(requirement.ProviderId, nameof(requirement.ProviderId));
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(requirement.Purpose);
        if (!Enum.IsDefined(requirement.CredentialKind))
        {
            throw new InvalidOperationException(
                $"Requirement '{requirement.Id}' uses unsupported credential kind '{requirement.CredentialKind}'.");
        }

        if (requirement.Scopes.Any(scope =>
                string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsWhiteSpace)))
        {
            throw new InvalidOperationException($"Requirement '{requirement.Id}' contains an empty scope.");
        }

        if (requirement.Scopes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != requirement.Scopes.Count)
        {
            throw new InvalidOperationException($"Requirement '{requirement.Id}' contains duplicate scopes.");
        }
    }

    public static void ValidateId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!IdPattern().IsMatch(value))
        {
            throw new ArgumentException(
                "IDs must start with a letter or number and contain only letters, numbers, periods, hyphens, or underscores.",
                parameterName);
        }
    }

    public static void ValidatePrincipalHandle(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 256 || value.Contains(':') || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Principal handles must be at most 256 characters and cannot contain colons or control characters.",
                parameterName);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
