using System;

namespace Yukari.Exceptions;

public class PluginVersionMismatchException : Exception
{
    public string PluginName { get; }

    public PluginVersionMismatchException(string pluginPath)
        : base(
            $"The plugin '{pluginPath}' is incompatible with this version of Mana. "
                + "Please download a compatible version of the plugin or update Mana."
        )
    {
        PluginName = pluginPath;
    }
}
