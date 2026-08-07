using System.Reflection;
using OpenCaddis.Server.Connections;

namespace OpenCaddis.Server;

public sealed class OpenCaddisServerHostOptions
{
    public string DisplayName { get; set; } = "OpenCaddis Server";

    public OpenCaddisCloudServerConnection? CloudServer { get; set; }

    public bool LoadAssembliesFromAddOnPath { get; set; } = true;

    public OpenCaddisConnectionRuntime? ConnectionRuntime { get; set; }

    public IList<Assembly> AdditionalAssemblies { get; } = [];
}
