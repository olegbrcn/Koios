using System.Diagnostics;
using Microsoft.Build.Locator;

namespace Koios.Core;

/// <summary>
/// Registers an MSBuild instance so <c>MSBuildWorkspace</c> can perform design-time
/// builds. This MUST run before any type that touches MSBuild/Roslyn workspaces is
/// JIT-loaded — Roslyn's MSBuild assemblies are resolved lazily via an
/// AssemblyResolve handler that RegisterDefaults installs. A static reference loaded
/// too early binds the wrong copy and crashes the engine.
///
/// Discovery is sensitive to the working directory's global.json: if it pins an SDK
/// that is not installed, MSBuildLocator's default discovery throws. We catch that
/// and fall back to an explicitly located installed SDK so the engine degrades
/// instead of crashing.
/// </summary>
public static class MSBuildBootstrap
{
    private static readonly object Gate = new();
    private static bool done;
    private static string description = "not-registered";

    public static string Register()
    {
        lock (Gate)
        {
            if (done)
                return description;

            if (MSBuildLocator.IsRegistered)
            {
                done = true;
                return description = "already-registered";
            }

            var pinned = Environment.GetEnvironmentVariable("KOIOS_MSBUILD_PATH");
            if (!string.IsNullOrWhiteSpace(pinned))
            {
                MSBuildLocator.RegisterMSBuildPath(pinned);
                done = true;
                return description = $"pinned {pinned}";
            }

            // Try the normal path first: highest VS/SDK instance for this directory.
            try
            {
                var current = MSBuildLocator.QueryVisualStudioInstances()
                    .OrderByDescending(i => i.Version)
                    .FirstOrDefault();
                if (current is not null)
                {
                    MSBuildLocator.RegisterInstance(current);
                    done = true;
                    return description = $"{current.Name} {current.Version}";
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.FileNotFoundException)
            {
                // Almost always: global.json pins an uninstalled SDK. Fall through.
            }

            // Fallback: register the highest installed SDK by path, ignoring global.json.
            var sdk = LocateHighestInstalledSdk();
            if (sdk is not null)
            {
                MSBuildLocator.RegisterMSBuildPath(sdk.Path);
                done = true;
                return description = $".NET SDK {sdk.Version} (fallback; global.json SDK unavailable)";
            }

            // Last resort: let RegisterDefaults throw a clear error.
            MSBuildLocator.RegisterDefaults();
            done = true;
            return description = "registered";
        }
    }

    private sealed record Sdk(string Version, string Path);

    private static Sdk? LocateHighestInstalledSdk()
    {
        // Parse `dotnet --list-sdks`: "10.0.104 [/usr/share/dotnet/sdk]"
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-sdks")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10_000);

            Sdk? best = null;
            Version? bestVer = null;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var open = line.IndexOf('[');
                var close = line.IndexOf(']');
                if (open < 0 || close < open) continue;
                var ver = line[..open].Trim();
                var baseDir = line[(open + 1)..close].Trim();
                var sdkDir = System.IO.Path.Combine(baseDir, ver);
                if (!System.IO.Directory.Exists(sdkDir)) continue;
                // Compare on the numeric prefix so previews sort sanely.
                var numeric = ver.Split('-')[0];
                if (!Version.TryParse(numeric, out var parsed)) continue;
                if (bestVer is null || parsed > bestVer)
                {
                    bestVer = parsed;
                    best = new Sdk(ver, sdkDir);
                }
            }
            return best;
        }
        catch { return null; }
    }
}
