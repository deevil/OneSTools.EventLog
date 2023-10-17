using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace OneSTools.EventLog.Exporter.Core
{
    public class FileLogger : ILogger
    {
        private static readonly object Locker = new object();
        private readonly string _categoryName;
        private readonly string _shortCN;
        private readonly string _path;

        public FileLogger(string path, string categoryName)
        {
            _path = path;
            _categoryName = categoryName;

            _shortCN = categoryName;
            if (_shortCN.StartsWith("OneSTools")) {
                _shortCN = _shortCN.Replace("OneSTools.EventLog.Exporter.Manager",  "EL_Manager");
                _shortCN = _shortCN.Replace("OneSTools.EventLog.Exporter.Core",     "EL_Core");
                _shortCN = _shortCN.Replace("OneSTools.EventLog.Exporter",          "EL_Exporter");
                _shortCN = _shortCN.Replace("OneSTools.EventLog",                   "EL_EventLog");

            }
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
            Func<TState, Exception, string> formatter)
        {
            var levelName = Enum.GetName(typeof(LogLevel), logLevel);
            var message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {levelName} | {_shortCN}[{eventId.Id}] | { formatter(state, exception)}";

            lock (Locker)
            {
                File.AppendAllText(_path, message + Environment.NewLine);
            }
        }
    }
}