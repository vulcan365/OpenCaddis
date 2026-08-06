using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

namespace OpenCaddis.Server.Builder.AI;

internal static class RoslynWorkspaceFactory
{
    private static readonly object RegistrationLock = new();

    public static MSBuildWorkspace Create()
    {
        EnsureMSBuildRegistered();
        return MSBuildWorkspace.Create();
    }

    private static void EnsureMSBuildRegistered()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        lock (RegistrationLock)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }
}
