using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Jellyfin.Plugin.TvGuide;

internal static class AssemblyResolver
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var pluginDir = Path.GetDirectoryName(typeof(AssemblyResolver).Assembly.Location);
        if (string.IsNullOrEmpty(pluginDir))
        {
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            var name = new AssemblyName(args.Name);
            var path = Path.Combine(pluginDir, name.Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
    }
}
