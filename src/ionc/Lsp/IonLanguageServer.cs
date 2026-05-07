namespace ion.compiler.Lsp;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Microsoft.Extensions.Logging;

public sealed class IonLanguageServer
{
    private readonly ILanguageServer _server;
    private readonly TcpClient? _tcpClient;

    private IonLanguageServer(ILanguageServer server, TcpClient? tcpClient = null)
    {
        _server = server;
        _tcpClient = tcpClient;
    }

    /// <summary>
    /// Start LSP server listening on TCP. Returns the actual port.
    /// </summary>
    public static async Task<(IonLanguageServer server, int port)> CreateTcpAsync(int requestedPort = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, requestedPort);
        listener.Start();
        var actualPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        Console.WriteLine($"[ionc] LSP server listening on port {actualPort}");

        // Write port to stderr so the client can read it
        await Console.Error.WriteLineAsync($"IONC_LSP_PORT={actualPort}");
        await Console.Error.FlushAsync();

        Console.WriteLine("[ionc] Waiting for client connection...");
        var client = await listener.AcceptTcpClientAsync();
        listener.Stop();
        Console.WriteLine("[ionc] Client connected");

        var stream = client.GetStream();
        var lsp = await CreateFromStreamsAsync(stream, stream);

        return (new IonLanguageServer(lsp._server, client), actualPort);
    }

    /// <summary>
    /// Start LSP server using raw streams (stdio fallback).
    /// </summary>
    public static Task<IonLanguageServer> CreateAsync(Stream input, Stream output)
        => CreateFromStreamsAsync(input, output);

    private static async Task<IonLanguageServer> CreateFromStreamsAsync(Stream input, Stream output)
    {
        var workspace = new IonWorkspace();

        var server = await LanguageServer.From(options =>
        {
            options
                .WithInput(input)
                .WithOutput(output)
                .ConfigureLogging(x => x.SetMinimumLevel(LogLevel.Warning))
                .WithServices(services =>
                {
                    services.AddSingleton(workspace);
                })
                .WithHandler<IonTextDocumentSyncHandler>()
                .WithHandler<IonHoverHandler>()
                .WithHandler<IonDocumentSymbolHandler>()
                .WithHandler<IonDefinitionHandler>()
                .WithHandler<IonReferencesHandler>()
                .WithHandler<IonCompletionHandler>()
                .WithHandler<IonSemanticTokensHandler>()
                .WithHandler<IonCodeActionHandler>()
                .WithHandler<IonRenameHandler>()
                // .WithHandler<IonInlayHintsHandler>()
                .WithHandler<IonCodeLensHandler>()
                .WithHandler<IonFoldingRangeHandler>()
                .WithHandler<IonDocumentLinkHandler>()
                .WithHandler<IonSignatureHelpHandler>()
                .WithHandler<IonWorkspaceSymbolsHandler>()
                .WithHandler<IonDocumentHighlightHandler>()
                .WithHandler<IonFormattingHandler>()
                .OnInitialize((server, request, token) =>
                {
                    Console.WriteLine($"[ionc] Initialize: rootUri={request.RootUri}, rootPath={request.RootPath}");
                    if (request.RootUri is not null)
                        workspace.SetRoot(request.RootUri.GetFileSystemPath());
                    else if (request.RootPath is not null)
                        workspace.SetRoot(request.RootPath);
                    return Task.CompletedTask;
                });
        }).ConfigureAwait(false);

        return new IonLanguageServer(server);
    }

    public async Task WaitForShutdownAsync()
    {
        await _server.WaitForExit;
        _tcpClient?.Dispose();
    }
}
