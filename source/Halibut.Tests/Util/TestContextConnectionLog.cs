﻿using System;
using System.Collections.Generic;
using System.Threading;
using Halibut.Diagnostics;
using Halibut.Logging;
using NUnit.Framework;
using ILog = Halibut.Diagnostics.ILog;

namespace Halibut.Tests.Util
{
    internal class TestContextConnectionLog : ILog
    {
        readonly string endpoint;
        readonly string name;

        public TestContextConnectionLog(string endpoint, string name)
        {
            this.endpoint = endpoint;
            this.name = name;
        }

        public void Write(EventType type, string message, params object[] args)
        {
            WriteInternal(new LogEvent(type, message, null, args));
        }

        public void WriteException(EventType type, string message, Exception ex, params object[] args)
        {
            WriteInternal(new LogEvent(type, message, ex, args));
        }

        public IList<LogEvent> GetLogs()
        {
            throw new NotImplementedException();
        }

        void WriteInternal(LogEvent logEvent)
        {
            var logLevel = GetLogLevel(logEvent);

            TestContext.WriteLine(string.Format("{6} {5, 16}: {0}:{1} {2}  {3} {4}", logLevel, logEvent.Error, endpoint, Thread.CurrentThread.ManagedThreadId, logEvent.FormattedMessage, name, DateTime.UtcNow.ToString("o")));
        }

        static LogLevel GetLogLevel(LogEvent logEvent)
        {
            switch (logEvent.Type)
            {
                case EventType.Error:
                    return LogLevel.Error;
                case EventType.Diagnostic:
                case EventType.SecurityNegotiation:
                case EventType.MessageExchange:
                    return LogLevel.Trace;
                case EventType.OpeningNewConnection:
                    return LogLevel.Debug;
                default:
                    return LogLevel.Info;
            }
        }
    }
}