using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodaTime;
using OneSTools.EventLog.Exporter.Core;
using OneSTools.EventLog.Exporter.Core.ClickHouse;
using OneSTools.EventLog.Exporter.Core.ElasticSearch;

namespace OneSTools.EventLog.Exporter.Manager
{
    public class ExportersManager : BackgroundService
    {
        private readonly List<ClstFolder> _clstFolders;
        private readonly List<ClstWatcher> _clstWatchers = new();

        private readonly int _collectedFactor;

        // ClickHouse
        private readonly string _connectionString;
        private readonly bool _loadArchive;
        private readonly ILogger<ExportersManager> _logger;
        private readonly int _maximumRetries;
        private readonly int _storeMode;

        private readonly TimeSpan _maxRetryTimeout;

        // ElasticSearch
        private readonly List<ElasticSearchNode> _nodes;
        private readonly int _portion;
        private readonly int _readingTimeout;
        private readonly Dictionary<string, CancellationTokenSource> _runExporters = new();
        private readonly HashSet<string> _skippedBases = new();
        private readonly string _separation;

        private readonly IServiceProvider _serviceProvider;

        // Common settings
        private readonly StorageType _storageType;
        private readonly DateTimeZone _timeZone = DateTimeZoneProviders.Tzdb.GetSystemDefault();
        private readonly int _writingMaxDop;
        private readonly DateTime _skipEventsBeforeDate;

        public ExportersManager(ILogger<ExportersManager> logger, IServiceProvider serviceProvider,
            IConfiguration configuration)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;

            _clstFolders = configuration.GetSection("Manager:ClstFolders").Get<List<ClstFolder>>();
            _storageType = configuration.GetValue("Exporter:StorageType", StorageType.None);
            _portion = configuration.GetValue("Exporter:Portion", 10000);
            _writingMaxDop = configuration.GetValue("Exporter:WritingMaxDegreeOfParallelism", 1);
            _collectedFactor = configuration.GetValue("Exporter:CollectedFactor", 2);
            _loadArchive = configuration.GetValue("Exporter:LoadArchive", false);
            _readingTimeout = configuration.GetValue("Exporter:ReadingTimeout", 1);
            _skipEventsBeforeDate = configuration.GetValue("Exporter:SkipEventsBeforeDate", DateTime.MinValue);

            var timeZone = configuration.GetValue("Exporter:TimeZone", "");

            if (!string.IsNullOrWhiteSpace(timeZone))
                _timeZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone) ??
                            throw new Exception($"\"{timeZone}\" is unknown time zone");

            CheckSettings();

            switch (_storageType)
            {
                case StorageType.ClickHouse:
                    {
                        _connectionString = configuration.GetValue("ClickHouse:ConnectionString", "");
                        _storeMode = configuration.GetValue("ClickHouse:StoreMode", 1);
                        if (_connectionString == string.Empty)
                            throw new Exception("Connection string is not specified");
                        break;
                    }
                case StorageType.ElasticSearch:
                    {
                        _nodes = configuration.GetSection("ElasticSearch:Nodes").Get<List<ElasticSearchNode>>();
                        if (_nodes == null)
                            throw new Exception("ElasticSearch nodes are not specified");

                        _separation = configuration.GetValue("ElasticSearch:Separation", "H");
                        _maximumRetries = configuration.GetValue("ElasticSearch:MaximumRetries",
                            ElasticSearchStorage.DefaultMaximumRetries);
                        _maxRetryTimeout = TimeSpan.FromSeconds(configuration.GetValue("ElasticSearch:MaxRetryTimeout",
                            ElasticSearchStorage.DefaultMaxRetryTimeoutSec));
                        break;
                    }
            }
        }

        private void CheckSettings()
        {
            if (_clstFolders == null || _clstFolders.Count == 0)
                throw new Exception("\"ClstFolders\" is not specified");

            foreach (var clstFolder in _clstFolders.Where(clstFolder => !Directory.Exists(clstFolder.Folder)))
                throw new Exception($"Clst folder ({clstFolder.Folder}) doesn't exist");

            if (_writingMaxDop <= 0)
                throw new Exception("WritingMaxDegreeOfParallelism cannot be equal to or less than 0");

            if (_collectedFactor <= 0)
                throw new Exception("CollectedFactor cannot be equal to or less than 0");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            stoppingToken.Register(() =>
            {
                lock (_runExporters)
                {
                    foreach (var ib in _runExporters)
                        ib.Value.Cancel();
                }
            });

            foreach (var clstFolder in _clstFolders)
            {
                var clstWatcher = new ClstWatcher(clstFolder.Folder, clstFolder.Templates);

                foreach (var (key, (name, dataBaseName)) in clstWatcher.InfoBases)
                    StartExporter(key, name, dataBaseName);

                clstWatcher.InfoBasesAdded += ClstWatcher_InfoBasesAdded;
                clstWatcher.InfoBasesDeleted += ClstWatcher_InfoBasesDeleted;

                _clstWatchers.Add(clstWatcher);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(60000, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                foreach (var watcher in _clstWatchers)
                {
                    foreach (var (key, (name, dataBaseName)) in watcher.InfoBases)
                    {
                        bool isRunning;
                        lock (_runExporters)
                        {
                            isRunning = _runExporters.ContainsKey(key);
                        }

                        if (!isRunning)
                            StartExporter(key, name, dataBaseName);
                    }
                }
            }
        }

        private void ClstWatcher_InfoBasesDeleted(object sender, ClstEventArgs args)
        {
            StopExporter(args.Path, args.Name);
        }

        private void ClstWatcher_InfoBasesAdded(object sender, ClstEventArgs args)
        {
            StartExporter(args.Path, args.Name, args.DataBaseName);
        }

        private void StartExporter(string path, string name, string dataBaseName)
        {
            var logFolder = Path.Combine(path, "1Cv8Log");

            if (!Directory.Exists(logFolder))
            {
                lock (_skippedBases)
                {
                    if (_skippedBases.Add(path))
                        _logger?.LogInformation($"Event log folder of \"{name}\" information base doesn't exist, skipping");
                }
                return;
            }

            var lgdPath = Path.Combine(logFolder, "1Cv8.lgd");
            var lgfPath = Path.Combine(logFolder, "1Cv8.lgf");
            var hasLgpFiles = Directory.EnumerateFiles(logFolder, "*.lgp").Any();

            var isPureSqlite = File.Exists(lgdPath) && !hasLgpFiles;
            var isMissingFiles = !File.Exists(lgfPath) && !hasLgpFiles;

            if (isPureSqlite || isMissingFiles)
            {
                lock (_skippedBases)
                {
                    if (_skippedBases.Add(path))
                    {
                        if (isPureSqlite)
                            _logger?.LogInformation(
                                $"Event log of \"{name}\" information base is in SQLite format (1Cv8.lgd) with no .lgp files, skipping");
                        else
                            _logger?.LogInformation(
                                $"Event log of \"{name}\" information base has no 1Cv8.lgf or .lgp files, skipping");
                    }
                }
                return;
            }

            lock (_skippedBases)
            {
                _skippedBases.Remove(path);
            }

            lock (_runExporters)
            {
                if (!_runExporters.ContainsKey(path))
                {
                    var cts = new CancellationTokenSource();
                    var logger =
                        (ILogger<EventLogExporter>)_serviceProvider.GetService(typeof(ILogger<EventLogExporter>));

                    var settings = new EventLogExporterSettings
                    {
                        LogFolder = logFolder,
                        CollectedFactor = _collectedFactor,
                        LoadArchive = _loadArchive,
                        Portion = _portion,
                        ReadingTimeout = _readingTimeout,
                        TimeZone = _timeZone,
                        WritingMaxDop = _writingMaxDop,
                        SkipEventsBeforeDate = _skipEventsBeforeDate
                    };

                    Task.Factory.StartNew(async () =>
                    {
                        while (!cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                using var storage = GetStorage(dataBaseName);
                                using var exporter = new EventLogExporter(settings, storage, logger, dataBaseName);
                                await exporter.StartAsync(cts.Token);
                            }
                            catch (TaskCanceledException)
                            {
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogCritical(ex, "Failed to execute EventLogExporter");
                            }

                            if (!cts.Token.IsCancellationRequested)
                            {
                                try
                                {
                                    await Task.Delay(5000, cts.Token);
                                }
                                catch (OperationCanceledException)
                                {
                                    break;
                                }
                            }
                        }
                    }, cts.Token);
                    _runExporters.Add(path, cts);

                    _logger?.LogInformation(
                        $"Event log exporter for \"{name}\" information base to \"{dataBaseName}\" is started");
                }
            }
        }

        private void StopExporter(string id, string name)
        {
            lock (_skippedBases)
            {
                _skippedBases.Remove(id);
            }

            lock (_runExporters)
            {
                if (_runExporters.TryGetValue(id, out var cts))
                {
                    cts.Cancel();
                    cts.Dispose();
                    _runExporters.Remove(id);
                    _logger?.LogInformation($"Event log exporter for \"{name}\" information base is stopped");
                }
            }
        }

        private IEventLogStorage GetStorage(string dataBaseName)
        {
            switch (_storageType)
            {
                case StorageType.ClickHouse:
                    {
                        var logger = (ILogger<ClickHouseStorage>)_serviceProvider.GetService(typeof(ILogger<ClickHouseStorage>));
                        //var connectionString = $"{_connectionString}Database={dataBaseName};";

                        return new ClickHouseStorage(_connectionString, logger,  dataBaseName, _storeMode);
                    }
                case StorageType.ElasticSearch:
                    {
                        var logger =
                            (ILogger<ElasticSearchStorage>)_serviceProvider.GetService(
                                typeof(ILogger<ElasticSearchStorage>));

                        var settings = new ElasticSearchStorageSettings
                        {
                            Index = dataBaseName,
                            Separation = _separation,
                            MaximumRetries = _maximumRetries,
                            MaxRetryTimeout = _maxRetryTimeout
                        };
                        settings.Nodes.AddRange(_nodes);

                        return new ElasticSearchStorage(settings, logger);
                    }
                case StorageType.None:
                    throw new Exception("StorageType parameter is not specified");
                default:
                    throw new Exception("Try to get a storage for unknown StorageType value");
            }
        }

        public override void Dispose()
        {
            base.Dispose();

            lock (_runExporters)
            {
                foreach (var ib in _runExporters)
                {
                    try
                    {
                        ib.Value.Cancel();
                        ib.Value.Dispose();
                    }
                    catch
                    {
                    }
                }
                _runExporters.Clear();
            }

            foreach (var clstWatcher in _clstWatchers)
                clstWatcher?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}