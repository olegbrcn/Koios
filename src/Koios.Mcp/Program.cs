using Koios.Core;
using Koios.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// The MCP host owns the Engine in-process: this is the long-lived resident an
// agent spawns for the session. stdout carries JSON-RPC only — all logging
// (ours and the SDK's) goes to stderr.

var solution = ResolveSolution(args);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(new EngineHost(solution));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<KoiosTools>();

var app = builder.Build();

// Kick off the cold load in the background so the initialize handshake answers
// immediately; tools await readiness (koios_status reports "loading" meanwhile).
app.Services.GetRequiredService<EngineHost>().Begin();

await app.RunAsync();
return 0;

static string ResolveSolution(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] is "-s" or "--solution")
            return args[i + 1];
    return Environment.GetEnvironmentVariable("KOIOS_SOLUTION") ?? Directory.GetCurrentDirectory();
}
