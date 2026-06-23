using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Koios.Core;

// Thin client: a short-lived CLI process round-trips one request to the resident.
public static class SocketClient
{
    /// <summary>True if a live server is accepting connections on this socket
    /// (a connect attempt, so a stale socket file reads as not running).</summary>
    public static bool IsRunning(string socketPath)
    {
        if (!File.Exists(socketPath)) return false;
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(socketPath));
            return true;
        }
        catch { return false; }
    }

    public static async Task<Envelope<T>> QueryAsync<T>(string socketPath, Request request, CancellationToken ct)
    {
        var line = await RoundTripAsync(socketPath, request, ct);
        return JsonSerializer.Deserialize<Envelope<T>>(line, Protocol.Wire)
               ?? throw new InvalidOperationException("Malformed response from resident server.");
    }

    public static async Task ShutdownAsync(string socketPath, CancellationToken ct) =>
        await RoundTripAsync(socketPath, new Request { Verb = Protocol.ShutdownVerb }, ct);

    private static async Task<string> RoundTripAsync(string socketPath, Request request, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await writer.WriteLineAsync(JsonSerializer.Serialize(request, Protocol.Wire));
        var line = await reader.ReadLineAsync(ct);
        return line ?? throw new InvalidOperationException("Resident server closed the connection without a response.");
    }
}
