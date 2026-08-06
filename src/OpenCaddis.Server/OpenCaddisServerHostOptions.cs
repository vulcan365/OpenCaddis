using System.Reflection;

namespace OpenCaddis.Server;

public sealed class OpenCaddisServerHostOptions
{
    public string DisplayName { get; set; } = "OpenCaddis Server";

    public OpenCaddisCloudServerConnection? CloudServer { get; set; }

    public bool LoadAssembliesFromAddOnPath { get; set; } = true;

    public IList<Assembly> AdditionalAssemblies { get; } = [];
}
