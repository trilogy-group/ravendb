﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests;
using Orders;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Backups;
using Raven.Server.Config;
using Tests.Infrastructure;
using Xunit;

namespace SlowTests.Issues
{
    public class RavenDB_12646 : RavenTestBase
    {
        [Fact]
        public async Task Backup_And_Restore_Revisions()
        {
            var backupPath = NewDataPath(suffix: "BackupFolder");

            using (var store = GetDocumentStore(new Options
            {
                ModifyDatabaseRecord = record => record.Settings[RavenConfiguration.GetKey(x => x.PerformanceHints.MaxNumberOfResults)] = "1"
            }))
            {
                store.Maintenance.Send(new CreateSampleDataOperation());

                WaitForIndexing(store);

                using (var session = store.OpenSession())
                {
                    session.Query<Employee>().ToList(); // this will generate performance hint
                }

                var database = await GetDatabase(store.Database);
                database.NotificationCenter.Paging.UpdatePaging(null);

                int beforeBackupAlertCount;
                using (database.NotificationCenter.GetStored(out var actions))
                    beforeBackupAlertCount = actions.Count();

                Assert.True(beforeBackupAlertCount > 0);

                var beforeBackupStats = store.Maintenance.Send(new GetStatisticsOperation());

                var config = new PeriodicBackupConfiguration
                {
                    BackupType = BackupType.Backup,
                    LocalSettings = new LocalSettings
                    {
                        FolderPath = backupPath
                    },
                    IncrementalBackupFrequency = "* * * * *" //every minute
                };

                var backupTaskId = (store.Maintenance.Send(new UpdatePeriodicBackupOperation(config))).TaskId;
                store.Maintenance.Send(new StartBackupOperation(true, backupTaskId));
                var operation = new GetPeriodicBackupStatusOperation(backupTaskId);
                SpinWait.SpinUntil(() =>
                {
                    var getPeriodicBackupResult = store.Maintenance.Send(operation);
                    return getPeriodicBackupResult.Status?.LastEtag > 0;
                }, TimeSpan.FromSeconds(15));

                // restore the database with a different name
                var restoredDatabaseName = GetDatabaseName();

                using (RestoreDatabase(store, new RestoreBackupConfiguration
                {
                    BackupLocation = Directory.GetDirectories(backupPath).First(),
                    DatabaseName = restoredDatabaseName
                }))
                {
                    var afterRestoreStats = store.Maintenance.ForDatabase(restoredDatabaseName).Send(new GetStatisticsOperation());
                    
                    Assert.Equal(beforeBackupStats.CountOfDocuments, afterRestoreStats.CountOfDocuments);
                    Assert.Equal(beforeBackupStats.CountOfDocumentsConflicts, afterRestoreStats.CountOfDocumentsConflicts);
                    Assert.Equal(beforeBackupStats.CountOfRevisionDocuments, afterRestoreStats.CountOfRevisionDocuments);
                    
                }
            }
        }
    }
}
