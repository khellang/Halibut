#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Halibut.Logging;
using Octopus.Shellfish;

namespace Halibut.Tests.Support.BackwardsCompatibility
{
    public class ProxyHalibutTestBinaryRunner
    {
        readonly ServiceConnectionType serviceConnectionType;
        readonly int? proxyClientListeningPort;
        readonly CertAndThumbprint clientCertAndThumbprint;
        readonly CertAndThumbprint serviceCertAndThumbprint;
        readonly string version;
        readonly ProxyDetails? proxyDetails;
        readonly string? webSocketPath;
        readonly LogLevel halibutLogLevel;
        readonly Uri? realServiceListenAddress;

        public ProxyHalibutTestBinaryRunner(
            ServiceConnectionType serviceConnectionType,
            int? proxyClientListeningPort,
            CertAndThumbprint clientCertAndThumbprint,
            CertAndThumbprint serviceCertAndThumbprint,
            Uri? realServiceListenAddress,
            string version,
            ProxyDetails? proxyDetails,
            string? webSocketPath,
            LogLevel halibutLogLevel)
        {
            this.serviceConnectionType = serviceConnectionType;
            this.proxyClientListeningPort = proxyClientListeningPort;
            this.clientCertAndThumbprint = clientCertAndThumbprint;
            this.serviceCertAndThumbprint = serviceCertAndThumbprint;
            this.version = version;
            this.proxyDetails = proxyDetails;
            this.halibutLogLevel = halibutLogLevel;
            this.webSocketPath = webSocketPath;
            this.realServiceListenAddress = realServiceListenAddress;
        }

        public async Task<RoundTripRunningOldHalibutBinary> Run()
        {
            var compatBinaryStayAlive = new CompatBinaryStayAlive();
            var settings = new Dictionary<string, string>
            {
                { "mode", "proxy" },
                { "tentaclecertpath", serviceCertAndThumbprint.CertificatePfxPath },
                { "octopuscertpath", clientCertAndThumbprint.CertificatePfxPath },
                { "websocketpath", webSocketPath ?? string.Empty },
                { "ServiceConnectionType", serviceConnectionType.ToString() },
                { "sslthubprint", Certificates.SslThumbprint },
                { "halibutloglevel", halibutLogLevel.ToString() },
                { CompatBinaryStayAlive.StayAliveFilePathEnvVarKey, compatBinaryStayAlive.LockFile }
            };

            if (proxyDetails != null)
            {
                settings.Add("proxydetails_host", proxyDetails.Host);
                settings.Add("proxydetails_password", proxyDetails.Password);
                settings.Add("proxydetails_username", proxyDetails.UserName);
                settings.Add("proxydetails_port", proxyDetails.Port.ToString());
                settings.Add("proxydetails_type", proxyDetails.Type.ToString());
            }

            if (proxyClientListeningPort != null)
            {
                settings.Add("octopusservercommsport", "https://localhost:" + proxyClientListeningPort);
            }

            if (realServiceListenAddress != null)
            {
                settings.Add("realServiceListenAddress", realServiceListenAddress.ToString());
            }

            var cts = new CancellationTokenSource();

            try
            {
                var tmp = new TmpDirectory();

                var (task, serviceListenPort, proxyClientListenPort) = await StartHalibutTestBinary(version, settings, tmp, cts.Token);

                return new RoundTripRunningOldHalibutBinary(cts, task, tmp, serviceListenPort, proxyClientListenPort, compatBinaryStayAlive);
            }
            catch (Exception)
            {
                cts.Cancel();
                throw;
            }
        }

        async Task<(Task, int?, int?)> StartHalibutTestBinary(string version, Dictionary<string, string> settings, TmpDirectory tmp, CancellationToken cancellationToken)
        {
            var hasTentacleStarted = new ManualResetEventSlim();
            hasTentacleStarted.Reset();

            var logger = new SerilogLoggerBuilder().Build().ForContext<ProxyHalibutTestBinaryRunner>();
            int? serviceListenPort = null;
            int? proxyClientListenPort = null;
            var runningTentacle = Task.Run(() =>
            {
                try
                {
                    void ProcessLogs(string s)
                    {
                        logger.Information(s);
                        if (s.StartsWith("Listening on port: "))
                        {
                            serviceListenPort = int.Parse(Regex.Match(s, @"\d+").Value);
                            logger.Information("External halibut binary listening port is: " + serviceListenPort);
                        }

                        if (s.StartsWith("Polling listener is listening on port: "))
                        {
                            proxyClientListenPort = int.Parse(Regex.Match(s, @"\d+").Value);
                        }

                        if (s.Contains("RunningAndReady")) hasTentacleStarted.Set();
                    }

                    ShellExecutor.ExecuteCommand(new HalibutTestBinaryPath().BinPath(version),
                        "",
                        tmp.FullPath,
                        ProcessLogs,
                        ProcessLogs,
                        ProcessLogs,
                        customEnvironmentVariables: settings,
                        cancel: cancellationToken
                    );
                }
                catch (Exception e)
                {
                    logger.Error(e, "Error waiting for external process to start");
                    throw;
                }
            }, cancellationToken);

            await Task.WhenAny(runningTentacle, Task.Run(() => { hasTentacleStarted.WaitHandle.WaitOne(TimeSpan.FromMinutes(1)); }));

            // Will throw.
            if (runningTentacle.IsCompleted) await runningTentacle;

            if (!hasTentacleStarted.IsSet)
            {
                throw new Exception("Halibut test binary did not appear to start correctly");
            }

            logger.Information("External halibut binary started.");
            return (runningTentacle, serviceListenPort, proxyClientListenPort);
        }

        public class RoundTripRunningOldHalibutBinary : IDisposable
        {
            readonly CancellationTokenSource cts;
            readonly Task runningOldHalibutTask;
            readonly TmpDirectory tmpDirectory;
            readonly CompatBinaryStayAlive compatBinaryStayAlive;

            public RoundTripRunningOldHalibutBinary(
                CancellationTokenSource cts,
                Task runningOldHalibutTask,
                TmpDirectory tmpDirectory,
                int? serviceListenPort,
                int? proxyClientListenPort,
                CompatBinaryStayAlive compatBinaryStayAlive)
            {
                this.cts = cts;
                this.runningOldHalibutTask = runningOldHalibutTask;
                this.tmpDirectory = tmpDirectory;
                ServiceListenPort = serviceListenPort;
                ProxyClientListenPort = proxyClientListenPort;
                this.compatBinaryStayAlive = compatBinaryStayAlive;
            }
            public int? ServiceListenPort { get; }
            public int? ProxyClientListenPort { get; }

            public void Dispose()
            {
                compatBinaryStayAlive.Dispose();
                cts.Cancel();
                runningOldHalibutTask.GetAwaiter().GetResult();
                tmpDirectory.Dispose();
            }
        }
    }
}