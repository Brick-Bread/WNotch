using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace Notch.Core.Plugins;

/// <summary>Loads a plugin's assembly and finds its <see cref="INotchPlugin"/> class.</summary>
internal static class PluginLoader
{
    /// <returns>Creates a new instance of the plugin each time the plugin is started.</returns>
    public static Func<INotchPlugin> Load(PluginManifest manifest, string directory)
    {
        string path = Path.Combine(directory, manifest.Assembly);
        if (!File.Exists(path))
        {
            throw new PluginLoadException($"The assembly '{manifest.Assembly}' named in {PluginManifest.FileName} was not found.");
        }

        CheckBuiltForThisNotch(manifest, path);

        Assembly assembly = new PluginLoadContext(manifest.Id, path).LoadFromAssemblyPath(path);
        Type[] entryPoints = [.. assembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(INotchPlugin).IsAssignableFrom(t))];

        if (entryPoints.Length != 1)
        {
            throw new PluginLoadException(entryPoints.Length == 0
                ? $"{manifest.Assembly} has no public class that implements {nameof(INotchPlugin)}."
                : $"{manifest.Assembly} has {entryPoints.Length} public classes that implement {nameof(INotchPlugin)}; there must be exactly one.");
        }

        Type entryPoint = entryPoints[0];
        if (entryPoint.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new PluginLoadException($"{entryPoint.FullName} needs a public parameterless constructor.");
        }

        return () => (INotchPlugin)Activator.CreateInstance(entryPoint)!;
    }

    /// <summary>
    /// Refuses a plugin built against a newer Notch than this one, which would otherwise fail
    /// with a .NET "could not load file or assembly" error naming no way out. Reads the
    /// assembly's metadata only; none of the plugin's code runs.
    /// </summary>
    internal static void CheckBuiltForThisNotch(PluginManifest manifest, string path)
    {
        Version? required = RequiredNotchVersion(path);
        Version host = typeof(INotchPlugin).Assembly.GetName().Version ?? new Version(0, 0);
        if (required is not null && required > host)
        {
            throw new PluginLoadException(
                $"{manifest.Name} was built for Notch {Trim(required)} or newer, but this is Notch {Trim(host)}. Update Notch, or install an older version of the plugin.");
        }

        static string Trim(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
    }

    private static Version? RequiredNotchVersion(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return null;
            }

            MetadataReader reader = pe.GetMetadataReader();
            foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
            {
                AssemblyReference reference = reader.GetAssemblyReference(handle);
                if (reader.GetString(reference.Name) == ContractAssemblyName)
                {
                    return reference.Version;
                }
            }
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or InvalidOperationException)
        {
            // Not readable here; loading it will report the real problem.
        }

        return null;
    }

    private static readonly string ContractAssemblyName = typeof(INotchPlugin).Assembly.GetName().Name!;

    /// <summary>
    /// Gives each plugin its own copies of the libraries it ships, so two plugins can use
    /// different versions of the same package. Never unloaded: the assemblies stay in the
    /// process until Notch exits, even when the plugin is switched off.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private static readonly string ContractAssembly = typeof(INotchPlugin).Assembly.GetName().Name!;

        private readonly string _directory;
        private readonly AssemblyDependencyResolver? _resolver;

        public PluginLoadContext(string pluginId, string assemblyPath)
            : base("plugin." + pluginId)
        {
            _directory = Path.GetDirectoryName(assemblyPath)!;
            try
            {
                _resolver = new AssemblyDependencyResolver(assemblyPath);
            }
            catch (InvalidOperationException)
            {
                // No usable .deps.json; fall back to looking next to the plugin.
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // The plugin must see the host's INotchPlugin, not a copy it happens to ship,
            // or its entry point would not be recognised as one.
            if (assemblyName.Name == ContractAssembly)
            {
                return null;
            }

            string? path = _resolver?.ResolveAssemblyToPath(assemblyName);
            if (path is null && assemblyName.Name is { } name)
            {
                string local = Path.Combine(_directory, name + ".dll");
                path = File.Exists(local) ? local : null;
            }

            // Null hands the request to the app, which supplies the framework and shared libraries.
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
