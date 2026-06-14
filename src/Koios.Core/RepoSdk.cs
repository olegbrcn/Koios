namespace Koios.Core;

/// <summary>
/// Some repos pin an SDK in global.json and install it into a repo-local
/// <c>.dotnet/</c> directory rather than system-wide. When the pinned SDK is not
/// installed system-wide, MSBuild's SDK resolver fails. This helper detects a
/// <c>.dotnet</c> at or above the target (including a user-level <c>~/.dotnet</c>)
/// and configures the process to use it, so the engine "just works" without the
/// operator wiring env vars by hand.
///
/// Must run BEFORE <see cref="MSBuildBootstrap.Register"/> (it sets the env vars that
/// bootstrap and the in-process SDK resolver read).
/// </summary>
public static class RepoSdk
{
    public static string? Configure(string startPath)
    {
        // Respect an explicit operator pin.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KOIOS_MSBUILD_PATH")))
            return null;

        var start = Directory.Exists(startPath)
            ? startPath
            : Path.GetDirectoryName(Path.GetFullPath(startPath)) ?? startPath;

        var dotnetRoot = FindRepoDotnet(start);
        if (dotnetRoot is null)
            return null;

        var sdkDir = HighestSdk(Path.Combine(dotnetRoot, "sdk"));
        if (sdkDir is null)
            return null;

        // The in-process SDK resolver consults this CLI dir to satisfy global.json,
        // bypassing the system dotnet (which lacks the pinned SDK).
        Environment.SetEnvironmentVariable("KOIOS_MSBUILD_PATH", sdkDir);
        Environment.SetEnvironmentVariable("DOTNET_ROOT", dotnetRoot);
        Environment.SetEnvironmentVariable("DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR", dotnetRoot);
        return sdkDir;
    }

    private static string? FindRepoDotnet(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".dotnet");
            if (Directory.Exists(Path.Combine(candidate, "sdk")) &&
                (File.Exists(Path.Combine(candidate, "dotnet")) ||
                 File.Exists(Path.Combine(candidate, "dotnet.exe"))))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? HighestSdk(string sdkRoot)
    {
        if (!Directory.Exists(sdkRoot))
            return null;
        string? best = null;
        Version? bestVer = null;
        foreach (var d in Directory.GetDirectories(sdkRoot))
        {
            var name = Path.GetFileName(d);
            var numeric = name.Split('-')[0];
            if (!Version.TryParse(numeric, out var v))
                continue;
            if (bestVer is null || v > bestVer)
            {
                bestVer = v;
                best = d;
            }
        }
        return best;
    }
}
