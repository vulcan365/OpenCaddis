namespace OpenCaddis.Server;

public enum OpenCaddisCloudTarget
{
    Server,
    ServerBuilder
}

public sealed record OpenCaddisCloudServerConnection(
    Uri CloudServerUri,
    string ApiKey,
    string ClusterId);
