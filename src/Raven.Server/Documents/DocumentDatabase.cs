﻿using System.Threading;
using Raven.Database.Util;
using Raven.Server.Config;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Patch;
using Raven.Server.ServerWide;
using Raven.Server.Utils.Metrics;
using Voron;

namespace Raven.Server.Documents
{
    public class DocumentDatabase : IResourceStore
    {
        private readonly CancellationTokenSource _databaseShutdown = new CancellationTokenSource();
        public readonly PatchDocument Patch;

        private readonly object _idleLocker = new object();

        public DocumentDatabase(string name, RavenConfiguration configuration, MetricsScheduler metricsScheduler=null)
        {
            Name = name;
            Configuration = configuration;

            Notifications = new DocumentsNotifications();
            DocumentsStorage = new DocumentsStorage(this);
            IndexStore = new IndexStore(this);
            DocumentTombstoneCleaner = new DocumentTombstoneCleaner(this);
            
            Metrics = new MetricsCountersManager(metricsScheduler??new MetricsScheduler());
            Patch = new PatchDocument(this);
        }

        public string Name { get; }

        public string ResourceName => $"db/{Name}";

        public RavenConfiguration Configuration { get; }

        public CancellationToken DatabaseShutdown => _databaseShutdown.Token;

        public DocumentsStorage DocumentsStorage { get; private set; }

        public DocumentTombstoneCleaner DocumentTombstoneCleaner { get; private set; }

        public DocumentsNotifications Notifications { get; }

        public MetricsCountersManager Metrics { get; }

        public IndexStore IndexStore { get; private set; }

        public void Initialize()
        {
            DocumentsStorage.Initialize();
            IndexStore.Initialize();
        }

        public void Initialize(StorageEnvironmentOptions options)
        {
            DocumentsStorage.Initialize(options);
            IndexStore.Initialize();
            DocumentTombstoneCleaner.Initialize();
        }

        public void Dispose()
        {
            _databaseShutdown.Cancel();
            IndexStore?.Dispose();
            IndexStore = null;

            DocumentTombstoneCleaner?.Dispose();
            DocumentTombstoneCleaner = null;

            DocumentsStorage?.Dispose();
            DocumentsStorage = null;
        }

        public void RunIdleOperations()
        {
            if (Monitor.TryEnter(_idleLocker) == false)
                return;

            try
            {
                IndexStore?.RunIdleOperations();
            }
            finally
            {
                Monitor.Exit(_idleLocker);
            }
        }
    }
}