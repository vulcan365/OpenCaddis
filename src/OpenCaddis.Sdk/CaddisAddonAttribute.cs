namespace OpenCaddis.Sdk;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CaddisAddonAttribute : Attribute
{
    public string Name { get; }
    public CaddisAddonAttribute(string name) => Name = name?.Trim() ?? string.Empty;
}
