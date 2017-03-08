#if HAS_WEB_SOCKET_LISTENER
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Halibut.Diagnostics;
using Halibut.Transport.Protocol;

namespace Halibut.Transport
{

    public class SecureWebSocketListener : IDisposable
    {
        readonly string endPoint;
        readonly X509Certificate2 serverCertificate;
        readonly Func<MessageExchangeProtocol, Task> protocolHandler;
        readonly Predicate<string> verifyClientThumbprint;
        readonly ILogFactory logFactory;
        readonly Func<string> getFriendlyHtmlPageContent;
        readonly CancellationTokenSource cts = new CancellationTokenSource();
        ILog log;
        HttpListener listener;

        public SecureWebSocketListener(string endPoint, X509Certificate2 serverCertificate, Action<MessageExchangeProtocol> protocolHandler, Predicate<string> verifyClientThumbprint, ILogFactory logFactory, Func<string> getFriendlyHtmlPageContent)
            : this(endPoint, serverCertificate, h => Task.Run(() => protocolHandler(h)), verifyClientThumbprint, logFactory, getFriendlyHtmlPageContent)

        {
        }

        public SecureWebSocketListener(string endPoint, X509Certificate2 serverCertificate, Func<MessageExchangeProtocol, Task> protocolHandler, Predicate<string> verifyClientThumbprint, ILogFactory logFactory, Func<string> getFriendlyHtmlPageContent)
        {
            this.endPoint = endPoint;
            this.serverCertificate = serverCertificate;
            this.protocolHandler = protocolHandler;
            this.verifyClientThumbprint = verifyClientThumbprint;
            this.logFactory = logFactory;
            this.getFriendlyHtmlPageContent = getFriendlyHtmlPageContent;
            EnsureCertificateIsValidForListening(serverCertificate);
        }

        public void Start()
        {
            listener = new HttpListener();
            listener.Prefixes.Add(endPoint);
            listener.Start();

            log = logFactory.ForEndpoint(new Uri("ws://example.com"));
            log.Write(EventType.ListenerStarted, "Listener started");
            Task.Run(async () => await Accept());
        }

        async Task Accept()
        {
            while (!cts.IsCancellationRequested)
            {
                using (cts.Token.Register(listener.Stop))
                {
                    try
                    {
                        var context = await listener.GetContextAsync();

                        if (context.Request.IsWebSocketRequest)
                        {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                            HandleClient(context);
#pragma warning restore CS4014
                        }
                        else
                        {
                            log.Write(EventType.Error, $"Rejected connection from {context.Request.RemoteEndPoint} as it is not Web Socket request");
                            SendFriendlyHtmlPage(context.Response.OutputStream);
                            context.Response.Close();
                        }

                    }
                    catch (Exception ex)
                    {
                        log.WriteException(EventType.Error, "Error accepting Web Socket client", ex);
                    }
                }
            }
        }

        async Task HandleClient(HttpListenerContext context)
        {
            try
            {
                log.Write(EventType.ListenerAcceptedClient, "Accepted Web Socket client: {0}", context.Request.RemoteEndPoint);
                await ExecuteRequest(context);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                log.WriteException(EventType.Error, "Error initializing TCP client", ex);
            }
        }

        async Task ExecuteRequest(HttpListenerContext listenerContext)
        {
            // By default we will close the stream to cater for failure scenarios
            var keepConnection = false;

            var clientName = listenerContext.Request.RemoteEndPoint;

            WebSocketStream webSocketStream = null;
            try
            {
                var webSocketContext = await listenerContext.AcceptWebSocketAsync("Octopus");
                webSocketStream = new WebSocketStream(webSocketContext.WebSocket);

                //log.Write(EventType.Security, "Secure connection established, client is not yet authenticated, client connected with {0}", listenerContext.SslProtocol.ToString());

                var req = await webSocketStream.ReadTextMessage(); // Initial message
                if (string.IsNullOrEmpty(req))
                {
                    log.Write(EventType.Diagnostic, "Ignoring empty request");
                    return;
                }

                if (req.Substring(0, 2) != "MX")
                {
                    log.Write(EventType.Diagnostic, "Appears to be a web browser, sending friendly HTML response");
                    return;
                }

                if (await Authorize(listenerContext, clientName))
                {
                    // Delegate the open stream to the protocol handler - we no longer own the stream lifetime
                    await ExchangeMessages(webSocketStream);

                    // Mark the stream as delegated once everything has succeeded
                    keepConnection = true;
                }
            }
            catch (Exception ex)
            {
                log.WriteException(EventType.Error, "Unhandled error when handling request from client: {0}", ex, clientName);
            }
            finally
            {
                if (!keepConnection)
                {
                    // Closing an already closed stream or client is safe, better not to leak
                    webSocketStream?.Dispose();
                    listenerContext.Response.Close();
                }
            }
        }

      
        void SendFriendlyHtmlPage(Stream stream)
        {
            var message = getFriendlyHtmlPageContent();

            // This could fail if the client terminates the connection and we attempt to write to it
            // Disposing the StreamWriter will close the stream - it owns the stream
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine("HTTP/1.0 200 OK");
                writer.WriteLine("Content-Type: text/html; charset=utf-8");
                writer.WriteLine("Content-Length: " + message.Length);
                writer.WriteLine();
                writer.WriteLine(message);
                writer.WriteLine();
                writer.Flush();
                stream.Flush();
            }
        }

        async Task<bool> Authorize(HttpListenerContext context, EndPoint clientName)
        {
            log.Write(EventType.Diagnostic, "Begin authorization");
            var certificate = await context.Request.GetClientCertificateAsync();
            if (certificate == null)
            {
                log.Write(EventType.ClientDenied, "A client at {0} connected, and attempted a message exchange, but did not present a client certificate", clientName);
                return true;
            }

            var thumbprint = new X509Certificate2(certificate.Export(X509ContentType.Cert)).Thumbprint;
            var isAuthorized = verifyClientThumbprint(thumbprint);

            if (!isAuthorized)
            {
                log.Write(EventType.ClientDenied, "A client at {0} connected, and attempted a message exchange, but it presented a client certificate with the thumbprint '{1}' which is not in the list of thumbprints that we trust", clientName, thumbprint);
                return false;
            }

            log.Write(EventType.Security, "Client at {0} authenticated as {1}", clientName, thumbprint);
            return true;
        }

        Task ExchangeMessages(WebSocketStream stream)
        {
            log.Write(EventType.Diagnostic, "Begin message exchange");

            return protocolHandler(new MessageExchangeProtocol(stream, log));
        }

        // ReSharper disable once UnusedParameter.Local
        static void EnsureCertificateIsValidForListening(X509Certificate2 certificate)
        {
            if (certificate == null) throw new Exception("No certificate was provided.");

            if (!certificate.HasPrivateKey)
            {
                throw new Exception("The X509 certificate provided does not have a private key, and so it cannot be used for listening.");
            }
        }


        public void Dispose()
        {
            cts.Cancel();
            listener.Stop();
            log.Write(EventType.ListenerStopped, "Listener stopped");
        }
    }
}
#endif