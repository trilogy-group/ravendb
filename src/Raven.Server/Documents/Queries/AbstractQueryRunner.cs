﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Client.Util.RateLimiting;
using Raven.Server.Documents.Handlers;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.TransactionCommands;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Voron.Global;
using PatchRequest = Raven.Server.Documents.Patch.PatchRequest;

namespace Raven.Server.Documents.Queries
{
    public abstract class AbstractQueryRunner
    {
        protected readonly DocumentDatabase Database;

        protected AbstractQueryRunner(DocumentDatabase database)
        {
            Database = database;
        }

        public Index GetIndex(string indexName)
        {
            var index = Database.IndexStore.GetIndex(indexName);
            if (index == null)
                IndexDoesNotExistException.ThrowFor(indexName);

            return index;
        }

        public abstract Task<DocumentQueryResult> ExecuteQuery(IndexQueryServerSide query, DocumentsOperationContext documentsContext, long? existingResultEtag, OperationCancelToken token);

        public abstract Task ExecuteStreamQuery(IndexQueryServerSide query, DocumentsOperationContext documentsContext, HttpResponse response,
            IStreamQueryResultWriter<Document> writer, OperationCancelToken token);

        public abstract Task ExecuteStreamIndexEntriesQuery(IndexQueryServerSide query, DocumentsOperationContext documentsContext, HttpResponse response,
            IStreamQueryResultWriter<BlittableJsonReaderObject> writer, OperationCancelToken token);

        public abstract Task<IndexEntriesQueryResult> ExecuteIndexEntriesQuery(IndexQueryServerSide query, DocumentsOperationContext context, long? existingResultEtag, OperationCancelToken token);

        public abstract Task<IOperationResult> ExecuteDeleteQuery(IndexQueryServerSide query, QueryOperationOptions options, DocumentsOperationContext context, Action<IOperationProgress> onProgress, OperationCancelToken token);

        public abstract Task<IOperationResult> ExecutePatchQuery(IndexQueryServerSide query, QueryOperationOptions options, PatchRequest patch,
            BlittableJsonReaderObject patchArgs, DocumentsOperationContext context, Action<IOperationProgress> onProgress, OperationCancelToken token);

        protected Task<IOperationResult> ExecuteDelete(IndexQueryServerSide query, Index index, QueryOperationOptions options, DocumentsOperationContext context, Action<DeterminateProgress> onProgress, OperationCancelToken token)
        {
            return ExecuteOperation(query, index, options, context, onProgress, (key, retrieveDetails) =>
            {
                var command = new DeleteDocumentCommand(key, null, Database);

                return new BulkOperationCommand<DeleteDocumentCommand>(command, retrieveDetails, x => new BulkOperationResult.DeleteDetails
                {
                    Id = key,
                    Etag = x.DeleteResult?.Etag
                });
            }, token);
        }

        protected Task<IOperationResult> ExecutePatch(IndexQueryServerSide query, Index index, QueryOperationOptions options, PatchRequest patch,
            BlittableJsonReaderObject patchArgs, DocumentsOperationContext context, Action<DeterminateProgress> onProgress, OperationCancelToken token)
        {
            return ExecuteOperation(query, index, options, context, onProgress, (key, retrieveDetails) =>
            {
                var command = new PatchDocumentCommand(context, key,
                    expectedChangeVector: null,
                    skipPatchIfChangeVectorMismatch: false,
                    patch: (patch, patchArgs),
                    patchIfMissing: (null, null),
                    database: Database,
                    debugMode: false,
                    isTest: false,
                    collectResultsNeeded: true,
                    returnDocument: false);

                return new BulkOperationCommand<PatchDocumentCommand>(command, retrieveDetails, x => new BulkOperationResult.PatchDetails
                {
                    Id = key,
                    ChangeVector = x.PatchResult.ChangeVector,
                    Status = x.PatchResult.Status
                });
            }, token);
        }

        private async Task<IOperationResult> ExecuteOperation<T>(IndexQueryServerSide query, Index index, QueryOperationOptions options,
    DocumentsOperationContext context, Action<DeterminateProgress> onProgress, Func<string, bool, BulkOperationCommand<T>> func, OperationCancelToken token)
    where T : TransactionOperationsMerger.MergedTransactionCommand
        {
            if (index.Type.IsMapReduce())
                throw new InvalidOperationException("Cannot execute bulk operation on Map-Reduce indexes.");

            query = ConvertToOperationQuery(query, options);

            const int batchSize = 1024;

            Queue<string> resultIds;
            try
            {
                var results = await index.Query(query, context, token).ConfigureAwait(false);
                if (options.AllowStale == false && results.IsStale)
                    throw new InvalidOperationException("Cannot perform bulk operation. Query is stale.");

                resultIds = new Queue<string>(results.Results.Count);

                foreach (var document in results.Results)
                {
                    resultIds.Enqueue(document.Id.ToString());
                }
            }
            finally // make sure to close tx if DocumentConflictException is thrown
            {
                context.CloseTransaction();
            }

            var progress = new DeterminateProgress
            {
                Total = resultIds.Count,
                Processed = 0
            };

            onProgress(progress);

            var result = new BulkOperationResult();

            using (var rateGate = options.MaxOpsPerSecond.HasValue ? new RateGate(options.MaxOpsPerSecond.Value, TimeSpan.FromSeconds(1)) : null)
            {
                while (resultIds.Count > 0)
                {
                    var command = new ExecuteRateLimitedOperations<string>(resultIds, id =>
                    {
                        var subCommand = func(id, options.RetrieveDetails);

                        if (options.RetrieveDetails)
                            subCommand.AfterExecute = details => result.Details.Add(details);

                        return subCommand;
                    }, rateGate, token, 
                        maxTransactionSize: 16 * Constants.Size.Megabyte,
                        batchSize: batchSize);

                    await Database.TxMerger.Enqueue(command);

                    progress.Processed += command.Processed;

                    onProgress(progress);

                    if (command.NeedWait)
                        rateGate?.WaitToProceed();
                }
            }

            result.Total = progress.Total;
            return result;
        }

        private static IndexQueryServerSide ConvertToOperationQuery(IndexQueryServerSide query, QueryOperationOptions options)
        {
            return new IndexQueryServerSide(query.Metadata)
            {
                Query = query.Query,
                Start = query.Start,
                WaitForNonStaleResultsTimeout = options.StaleTimeout ?? query.WaitForNonStaleResultsTimeout,
                PageSize = int.MaxValue,
                QueryParameters = query.QueryParameters
            };
        }

        internal class BulkOperationCommand<T> : TransactionOperationsMerger.MergedTransactionCommand where T : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly T _command;
            private readonly bool _retrieveDetails;
            private readonly Func<T, IBulkOperationDetails> _getDetails;

            public BulkOperationCommand(T command, bool retrieveDetails, Func<T, IBulkOperationDetails> getDetails)
            {
                _command = command;
                _retrieveDetails = retrieveDetails;
                _getDetails = getDetails;
            }

            public override int Execute(DocumentsOperationContext context, TransactionOperationsMerger.RecordingState recording)
            {
                var count = _command.Execute(context, recording);

                if (_retrieveDetails)
                    AfterExecute?.Invoke(_getDetails(_command));

                return count;
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto(JsonOperationContext context)
            {
                throw new NotSupportedException($"ToDto() of {nameof(BulkOperationCommand<T>)} Should not be called");
            }

            protected override int ExecuteCmd(DocumentsOperationContext context)
            {
                throw new NotSupportedException("Should only call Execute() here");
            }

            public Action<IBulkOperationDetails> AfterExecute { private get; set; }
        }
    }
}
