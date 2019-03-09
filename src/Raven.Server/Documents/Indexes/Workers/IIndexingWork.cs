﻿using System;
using System.Threading;
using Raven.Server.Documents.Indexes.Persistence.Lucene;
using Raven.Server.ServerWide.Context;

namespace Raven.Server.Documents.Indexes.Workers
{
    public interface IIndexingWork
    {
        string Name { get; }

        bool Execute(DocumentsOperationContext databaseContext, TransactionOperationContext indexContext,
                     Lazy<IndexWriteOperation> writeOperation, IndexingStatsScope stats, CancellationToken token);

        bool CanContinueBatch(DocumentsOperationContext documentsContext, TransactionOperationContext indexingContext, 
            IndexingStatsScope stats, IndexWriteOperation indexWriteOperation, long currentEtag, long maxEtag, int count);
    }
}
