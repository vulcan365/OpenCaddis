using System.Reflection;

namespace OpenCaddis.Server;

public sealed class OpenCaddisServerHostOptions
{
    public string DisplayName { get; set; } = "OpenCaddis Server";

    public IList<Assembly> AdditionalAssemblies { get; } = [];
}
