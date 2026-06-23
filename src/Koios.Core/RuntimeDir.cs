using System.Security.Cryptography;
using System.Text;

namespace Koios.Core;

// Where resident-server sockets live, and how a solution maps to a socket name.
// One resident per absolute solution path → one stable, collision-free socket file.
public static class RuntimeDir
{
    public static string Dir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg)) return xdg;
        var tmp = Environment.GetEnvironmentVariable("TMPDIR");
        if (!string.IsNullOrEmpty(tmp) && Directory.Exists(tmp)) return tmp;
        return "/tmp";
    }

    /// <summary>Stable per-solution socket path: {runtimeDir}/koios-{sha256(absPath)[..16]}.sock</summary>
    public static string SocketPathFor(string solutionPathOrDir)
    {
        var abs = Path.GetFullPath(solutionPathOrDir);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(abs));
        var hex = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        return Path.Combine(Dir(), $"koios-{hex}.sock");
    }
}
