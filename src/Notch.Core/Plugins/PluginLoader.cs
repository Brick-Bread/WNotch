using System.Reflection;
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
