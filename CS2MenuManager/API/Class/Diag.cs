using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace CS2MenuManager.API.Class;

/// <summary>
/// osnet: small diagnostic logger. Lines start with "[CS2MM.osnet]" so they can be grepped from the server log.
/// Logging must never break menu cleanup, so every call swallows its own errors.
/// </summary>
internal static class Diag
{
    private const string Prefix = "[CS2MM.osnet] ";

    internal static void Info(BasePlugin? plugin, string message)
    {
        try
        {
            if (plugin?.Logger is { } logger) logger.LogInformation("{Message}", Prefix + message);
            else Console.WriteLine(Prefix + message);
        }
        catch
        {
            // ignored on purpose
        }
    }

    internal static void Warn(BasePlugin? plugin, string message)
    {
        try
        {
            if (plugin?.Logger is { } logger) logger.LogWarning("{Message}", Prefix + message);
            else Console.WriteLine(Prefix + "WARN " + message);
        }
        catch
        {
            // ignored on purpose
        }
    }
}
