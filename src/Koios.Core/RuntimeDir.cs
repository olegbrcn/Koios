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
        var baseDir = !string.IsNullOrEmpty(tmp) && Directory.Exists(tmp) ? tmp : "/tmp";
        return PrivateSubdir(baseDir);
    }

    // XDG_RUNTIME_DIR is per-user 0700 by spec, but TMPDIR//tmp may be shared with
    // other local users — keep sockets in a private per-user subdir there so nobody
    // else can connect to (or swap out) a resident's socket.
    static string PrivateSubdir(string baseDir)
    {
        var dir = Path.Combine(baseDir, $"koios-{Environment.UserName}");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(dir, Private);
        if (new DirectoryInfo(dir).LinkTarget is not null)
            throw new IOException($"Refusing to use '{dir}': it is a symlink.");
        if (File.GetUnixFileMode(dir) != Private)
            File.SetUnixFileMode(dir, Private); // pre-existing dir we don't own → UnauthorizedAccessException, deliberately loud
        return dir;
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
