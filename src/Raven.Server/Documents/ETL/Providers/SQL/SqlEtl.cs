﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Raven.Client.Documents.Operations.Counters;
using Raven.Client.Documents.Operations.ETL;
using Raven.Client.Documents.Operations.ETL.SQL;
using Raven.Server.Documents.ETL.Providers.SQL.Enumerators;
using Raven.Server.Documents.ETL.Providers.SQL.Metrics;
using Raven.Server.Documents.ETL.Providers.SQL.RelationalWriters;
using Raven.Server.Documents.ETL.Providers.SQL.Test;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;

namespace Raven.Server.Documents.ETL.Providers.SQL
{
    public class SqlEtl : EtlProcess<ToSqlItem, SqlTableWithRecords, SqlEtlConfiguration, SqlConnectionString>
    {
        public const string SqlEtlTag = "SQL ETL";

        public readonly SqlEtlMetricsCountersManager SqlMetrics = new SqlEtlMetricsCountersManager();

        public SqlEtl(Transformation transformation, SqlEtlConfiguration configuration, DocumentDatabase database, ServerStore serverStore)
            : base(transformation, configuration, database, serverStore, SqlEtlTag)
        {
            Metrics = SqlMetrics;
        }

        protected override IEnumerator<ToSqlItem> ConvertDocsEnumerator(IEnumerator<Document> docs, string collection)
        {
            return new DocumentsToSqlItems(docs, collection);
        }

        protected override IEnumerator<ToSqlItem> ConvertTombstonesEnumerator(IEnumerator<Tombstone> tombstones, string collection, EtlItemType type)
        {
            return new TombstonesToSqlItems(tombstones, collection);
        }

        protected override IEnumerator<ToSqlItem> ConvertCountersEnumerator(IEnumerator<CounterDetail> counters, string collection)
        {
            throw new NotSupportedException("Counters aren't supported by SQL ETL");
        }

        protected override bool ShouldTrackAttachmentTombstones()
        {
            return false;
        }

        public override bool ShouldTrackCounters()
        {
            return false;
        }

        protected override EtlTransformer<ToSqlItem, SqlTableWithRecords> GetTransformer(DocumentsOperationContext context)
        {
            return new SqlDocumentTransformer(Transformation, Database, context, Configuration);
        }

        protected override void LoadInternal(IEnumerable<SqlTableWithRecords> records, JsonOperationContext context)
        {
            using (var writer = new RelationalDatabaseWriter(this, Database))
            {
                foreach (var table in records)
                {
                    var stats = writer.Write(table, null, CancellationToken);

                    LogStats(stats, table);
                }

                writer.Commit();
            }
        }

        private void LogStats(SqlWriteStats stats, SqlTableWithRecords table)
        {
            if (table.Inserts.Count > 0)
            {
                if (Logger.IsInfoEnabled)
                {
                    Logger.Info($"[{Name}] Inserted {stats.InsertedRecordsCount} (out of {table.Inserts.Count}) records to '{table.TableName}' table " +
                        $"from the following documents: {string.Join(", ", table.Inserts.Select(x => x.DocumentId))}");
                }
            }

            if (table.Deletes.Count > 0)
            {
                if (Logger.IsInfoEnabled)
                {
                    Logger.Info($"[{Name}] Deleted {stats.DeletedRecordsCount} (out of {table.Deletes.Count}) records from '{table.TableName}' table " +
                        $"for the following documents: {string.Join(", ", table.Inserts.Select(x => x.DocumentId))}");
                }
            }
        }

        protected override bool ShouldFilterOutHiLoDocument()
        {
            return true;
        }

        public SqlEtlTestScriptResult RunTest(DocumentsOperationContext context, IEnumerable<SqlTableWithRecords> toWrite, bool performRolledBackTransaction)
        {
            var summaries = new List<TableQuerySummary>();
            
            if (performRolledBackTransaction)
            {
                try
                {
                    using (var writer = new RelationalDatabaseWriter(this, Database))
                    {
                        foreach (var records in toWrite)
                        {
                            var commands = new List<DbCommand>();

                            writer.Write(records, commands, CancellationToken);

                            summaries.Add(TableQuerySummary.GenerateSummaryFromCommands(records.TableName, commands));
                        }

                        writer.Rollback();
                    }
                }
                catch (Exception e)
                {
                    Statistics.RecordLoadError(e.ToString(), documentId: null, count: 1);
                }
            }
            else
            {
                var simulatedWriter = new RelationalDatabaseWriterSimulator(Configuration);

                foreach (var records in toWrite)
                {
                    var commands = simulatedWriter.SimulateExecuteCommandText(records, CancellationToken).Select(x => new TableQuerySummary.CommandData
                    {
                        CommandText = x
                    }).ToArray();

                    summaries.Add(new TableQuerySummary
                    {
                        TableName = records.TableName,
                        Commands = commands
                    });
                }
            }

            return new SqlEtlTestScriptResult
            {
                TransformationErrors = Statistics.TransformationErrorsInCurrentBatch.Errors.ToList(),
                LoadErrors = Statistics.LastLoadErrorsInCurrentBatch.Errors.ToList(),
                SlowSqlWarnings = Statistics.LastSlowSqlWarningsInCurrentBatch.Statements.ToList(),
                Summary = summaries
            };
        }
    }
}
