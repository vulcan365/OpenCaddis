using System.Reflection;
using System.Runtime.Loader;

namespace OpenCaddis.Server;

internal sealed class AddOnAssemblyCatalog : IDisposable
{
    private AddOnLoadContext? loadContext;
    private readonly List<Assembly> assemblies;
    private bool disposed;

    private AddOnAssemblyCatalog(AddOnLoadContext? loadContext, List<Assembly> assemblies)
    {
        this.loadContext = loadContext;
        this.assemblies = assemblies;
    }

    public IReadOnlyList<Assembly> Assemblies => assemblies;

    public static AddOnAssemblyCatalog Empty() => new(null, []);

    public static AddOnAssemblyCatalog Load(string addOnPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(addOnPath);

        var fullPath = Path.GetFullPath(addOnPath);
        Directory.CreateDirectory(fullPath);

        var assemblyPaths = DiscoverManagedAssemblies(fullPath);
        var loadContext = new AddOnLoadContext(assemblyPaths);
        var loadedAssemblies = new List<Assembly>();

        try
        {
            foreach (var assemblyPath in assemblyPaths.Values.Order(StringComparer.OrdinalIgnoreCase))
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                if (TryGetSharedAssembly(assemblyName) is not null)
                {
                    continue;
                }

                var assembly = loadContext.LoadManagedAssembly(assemblyPath);
                if (!loadedAssemblies.Contains(assembly))
                {
                    loadedAssemblies.Add(assembly);
                }
            }

            return new AddOnAssemblyCatalog(loadContext, loadedAssemblies);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        assemblies.Clear();
        AddOnLoadContext? context = loadContext;
        loadContext = null;
        if (context is null)
        {
            return;
        }

        var contextReference = new WeakReference(context, trackResurrection: false);
        context.Unload();
        context = null;

        // A restart must not retain assemblies that were removed from the add-on path.
        // Complete the collectible-context unload before the next host is created.
        for (var attempt = 0; contextReference.IsAlive && attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static Dictionary<string, string> DiscoverManagedAssemblies(string addOnPath)
    {
        var assemblyPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in Directory.EnumerateFiles(addOnPath, "*.dll", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            AssemblyName assemblyName;
            try
            {
                assemblyName = AssemblyName.GetAssemblyName(filePath);
            }
            catch (BadImageFormatException)
            {
                // Native runtime libraries can live beside managed add-ons.
                continue;
            }

            var simpleName = assemblyName.Name
                ?? throw new InvalidOperationException($"The assembly at '{filePath}' has no name.");

            if (assemblyPaths.TryGetValue(simpleName, out var existingPath))
            {
                var existingIdentity = identities[simpleName];
                if (!string.Equals(existingIdentity, assemblyName.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Add-on assembly conflict for '{simpleName}': '{existingPath}' and '{filePath}' have different identities.");
                }

                continue;
            }

            assemblyPaths.Add(simpleName, Path.GetFullPath(filePath));
            identities.Add(simpleName, assemblyName.FullName ?? simpleName);
        }

        return assemblyPaths;
    }

    private static Assembly? TryGetSharedAssembly(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null ||
            !(name.Equals("OpenCaddis.Server", StringComparison.OrdinalIgnoreCase) ||
              name.StartsWith("FabrCore.", StringComparison.OrdinalIgnoreCase) ||
              name.StartsWith("Orleans.", StringComparison.OrdinalIgnoreCase) ||
              name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
              name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
              name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
            candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
        if (loaded is not null)
        {
            return loaded;
        }

        try
        {
            // Add-ons compiled against FabrCore.Sdk carry Orleans source-generator metadata.
            // Resolve all host-owned dependencies explicitly through the default context even
            // when startup has not loaded that particular assembly yet.
            return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    private sealed class AddOnLoadContext : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, string> assemblyPaths;

        public AddOnLoadContext(IReadOnlyDictionary<string, string> assemblyPaths)
            : base($"OpenCaddis.AddOns.{Guid.NewGuid():N}", isCollectible: true)
        {
            this.assemblyPaths = assemblyPaths;
        }

        public Assembly LoadManagedAssembly(string assemblyPath)
        {
            var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
            var existing = Assemblies.FirstOrDefault(
                candidate => AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
            return existing ?? LoadFromFileWithoutLock(assemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var sharedAssembly = TryGetSharedAssembly(assemblyName);
            if (sharedAssembly is not null)
            {
                return sharedAssembly;
            }

            return assemblyName.Name is not null && assemblyPaths.TryGetValue(assemblyName.Name, out var assemblyPath)
                ? LoadFromFileWithoutLock(assemblyPath)
                : null;
        }

        private Assembly LoadFromFileWithoutLock(string assemblyPath)
        {
            using var assemblyStream = File.OpenRead(assemblyPath);
            var symbolsPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (!File.Exists(symbolsPath))
            {
                return LoadFromStream(assemblyStream);
            }

            using var symbolsStream = File.OpenRead(symbolsPath);
            return LoadFromStream(assemblyStream, symbolsStream);
        }
    }
}
