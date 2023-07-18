﻿using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    static class IsExternalInit { }
}
#endif

namespace Halibut.TestProxy
{
    public record HttpProxyOptions
    {
        public string Listen { get; set; } = "0.0.0.0:0";
    }

    record HttpProxyConnectionRequest(ProxyEndpoint Endpoint, string HttpVersion);

    public class HttpProxyService : BackgroundService
    {
        readonly HttpProxyOptions options;
        readonly ILogger<HttpProxyService> logger;
        readonly IProxyConnectionService proxyConnectionService;

        public HttpProxyService(HttpProxyOptions options, ILoggerFactory loggerFactory)
        {
            this.options = options;
            this.proxyConnectionService = new ProxyConnectionService(new ProxyConnectionFactory(loggerFactory), loggerFactory.CreateLogger<ProxyConnectionService>());
            this.logger = loggerFactory.CreateLogger<HttpProxyService>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var listener = await TcpListenerHelpers.GetTcpListener(options.Listen);
            listener.Start();
            Endpoint = (IPEndPoint)listener.LocalEndpoint;

            logger.LogInformation("Listening for HTTP proxy requests on {Listen}", listener.Server.LocalEndPoint);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        TcpClient? client;

#if DOES_NOT_SUPPORT_CANCELLATION_ON_SOCKETS
                        using var socketCancellationTokenSource = new CancellationTokenSource();
                        using (stoppingToken.Register(() =>
                               {
                                   try
                                   {
                                       socketCancellationTokenSource.Cancel();
                                   }
                                   catch
                                   {
                                   }

                               }))
                        {
                            var cancelTask = socketCancellationTokenSource.Token.AsTask<TcpClient?>();
                            var actionTask = Task.Run(() => listener.AcceptTcpClientAsync(), stoppingToken);

                            client = await (await Task.WhenAny(actionTask, cancelTask).ConfigureAwait(false)).ConfigureAwait(false);
                        }
#else
                    client = await listener.AcceptTcpClientAsync(stoppingToken);
#endif
                        _ = Task.Run(async () => await HandleProxyRequest(client!, stoppingToken), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Ignore, we are being shutdown
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        public IPEndPoint? Endpoint { get; set; }

        async Task HandleProxyRequest(TcpClient client, CancellationToken cancellationToken)
        {
        if (cancellationToken.IsCancellationRequested) return;

        var stream = client.GetStream();
        if (client.Client.RemoteEndPoint is not IPEndPoint sourceRemoteEndpoint)
        {
            throw new InvalidOperationException($"{client.Client.RemoteEndPoint} is not an {nameof(IPEndPoint)}");
        }

        var sourceEndpoint = new ProxyEndpoint(sourceRemoteEndpoint.Address.ToString(), sourceRemoteEndpoint.Port);

        // Set up some pipes to handle reading and replying to the connect request
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));

        try
        {
            HttpProxyConnectionRequest? connectionRequest = null;
            try
            {
                connectionRequest = await ParseConnectionRequest(sourceEndpoint, reader, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error has occurred parsing CONNECT request from {SourceEndpoint}", sourceEndpoint);
                await SendConnectResponse(sourceEndpoint, "1.0", 400, "Bad request", writer, cancellationToken);
                return;
            }

            try
            {
                await CreateProxyConnectionAndForward(client, connectionRequest, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError("An error has occurred establishing proxy connection: {SourceEndpoint} <-> {DestinationEndpoint}. Error: {ErrorMessage}", sourceEndpoint, connectionRequest!.Endpoint, ex.Message);
                await SendConnectResponse(sourceEndpoint, connectionRequest.HttpVersion, 502, "Bad gateway", writer, cancellationToken);
                return;
            }

            try
            {
                await SendConnectResponse(sourceEndpoint, connectionRequest.HttpVersion, 200, "Connection established", writer, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An error has occurred responding to CONNECT request: {SourceEndpoint} <-> {DestinationEndpoint}", sourceEndpoint, connectionRequest!.Endpoint);
                return;
            }

            logger.LogInformation("CONNECT request successful: {SourceEndpoint} -> {DestinationEndpoint}", sourceEndpoint, connectionRequest.Endpoint);
        }
        finally
        {
            await reader.CompleteAsync();
            await writer.CompleteAsync();
        }

        }

        async Task CreateProxyConnectionAndForward(TcpClient client, HttpProxyConnectionRequest connectionRequest, CancellationToken cancellationToken)
        {
            var proxyConnection = proxyConnectionService.GetProxyConnection(connectionRequest.Endpoint);
            await proxyConnection.Connect(client, cancellationToken);
        }

        async Task SendConnectResponse(ProxyEndpoint endpoint, string httpVersion, int responseCode, string message, PipeWriter writer, CancellationToken cancellationToken)
        {
            var response = $"HTTP/{httpVersion} {responseCode} {message}\r\n\r\n";
            logger.LogTrace("Sending {ResponseCode} response to CONNECT request to {SourceEndpoint}: {Message}", responseCode, endpoint, message);
            await writer.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken);
        }

        async Task<HttpProxyConnectionRequest> ParseConnectionRequest(ProxyEndpoint sourceEndpoint, PipeReader reader, CancellationToken cancellationToken)
        {
            var connectionRequestReceived = false;
            string? hostname = null;
            int? port = null;
            string? httpVersion = null;

            var connectRequestRegex = new Regex("CONNECT (?<hostname>[\\S]+):(?<port>\\d+) HTTP/(?<version>\\d{1}\\.{1}\\d{1})", RegexOptions.Compiled);

            while (!connectionRequestReceived && !cancellationToken.IsCancellationRequested)
            {
                var readResult = await reader.ReadAsync(cancellationToken);
                var buffer = readResult.Buffer;

                try
                {
                    if (readResult.IsCanceled)
                    {
                        break;
                    }

                    if (TryParseLines(ref buffer, out var message))
                    {
                        var match = connectRequestRegex.Match(message);
                        if (match.Success)
                        {
                            hostname = match.Groups["hostname"].Value;
                            port = int.Parse(match.Groups["port"].Value);
                            httpVersion = match.Groups["version"].Value;

                            connectionRequestReceived = true;
                        }
                        else
                            throw new InvalidOperationException("Connection request must start with CONNECT");
                    }
                }
                finally
                {
                    reader.AdvanceTo(buffer.Start, buffer.End);
                }
            }

            var destinationEndpoint = new ProxyEndpoint(hostname!, port!.Value);
            logger.LogInformation("CONNECT request received: {SourceEndpoint} -> {DestinationEndpoint}", sourceEndpoint, destinationEndpoint);

            return new HttpProxyConnectionRequest(destinationEndpoint, httpVersion!);
        }

        static bool TryParseLines(ref ReadOnlySequence<byte> buffer, out string message)
        {
            SequencePosition? position;
            StringBuilder outputMessage = new();

            while (true)
            {
                position = buffer.PositionOf((byte)'\n');

                if (!position.HasValue)
                    break;

                outputMessage.Append(Encoding.ASCII.GetString(buffer.Slice(buffer.Start, position.Value).ToArray()))
                             .AppendLine();

                buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
            };

            message = outputMessage.ToString();
            return message.Length != 0;
        }

        public override void Dispose()
        {
            proxyConnectionService.Dispose();

            base.Dispose();
        }
    }
}