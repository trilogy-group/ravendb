﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Jint.Native;
using Jint.Native.Object;
using Raven.Client;
using Raven.Client.Documents.Indexes;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Client.Extensions;
using Raven.Server.Documents.Indexes;
using Raven.Server.Documents.Indexes.Debugging;
using Raven.Server.Documents.Indexes.Errors;
using Raven.Server.Documents.Indexes.MapReduce.Static;
using Raven.Server.Documents.Indexes.Static;
using Raven.Server.Documents.Patch;
using Raven.Server.Documents.Queries;
using Raven.Server.Documents.Queries.Dynamic;
using Raven.Server.Json;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.NotificationCenter.Notifications.Details;
using Raven.Server.Routing;
using Raven.Server.ServerWide;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;

namespace Raven.Server.Documents.Handlers
{
    public class IndexHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/indexes/replace", "POST", AuthorizationStatus.ValidUser)]
        public Task Replace()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            var replacementName = Constants.Documents.Indexing.SideBySideIndexNamePrefix + name;

            var oldIndex = Database.IndexStore.GetIndex(name);
            var newIndex = Database.IndexStore.GetIndex(replacementName);

            if (oldIndex == null && newIndex == null)
                throw new IndexDoesNotExistException($"Could not find '{name}' and '{replacementName}' indexes.");

            if (newIndex == null)
                throw new IndexDoesNotExistException($"Could not find side-by-side index for '{name}'.");

            while (Database.DatabaseShutdown.IsCancellationRequested == false)
            {
                if (Database.IndexStore.TryReplaceIndexes(name, newIndex.Name, Database.DatabaseShutdown))
                    break;
            }

            return NoContent();
        }

        [RavenAction("/databases/*/indexes/source", "GET", AuthorizationStatus.ValidUser)]
        public Task Source()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            var index = Database.IndexStore.GetIndex(name);
            if (index == null)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            if (index.Type.IsStatic() == false)
                throw new InvalidOperationException("Source can be only retrieved for static indexes.");

            string source = null;
            switch (index.Type)
            {
                case IndexType.Map:
                    var staticMapIndex = (MapIndex)index;
                    source = staticMapIndex._compiled.Source;
                    break;
                case IndexType.MapReduce:
                    var staticMapReduceIndex = (MapReduceIndex)index;
                    source = staticMapReduceIndex._compiled.Source;
                    break;
            }

            if (string.IsNullOrWhiteSpace(source))
                throw new InvalidOperationException("Could not retrieve source for given index.");

            using (ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                context.Write(writer, new DynamicJsonValue
                {
                    ["Index"] = index.Name,
                    ["Source"] = source
                });
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/has-changed", "POST", AuthorizationStatus.ValidUser)]
        public Task HasChanged()
        {
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var json = context.ReadForMemory(RequestBodyStream(), "index/definition"))
            {
                var indexDefinition = JsonDeserializationServer.IndexDefinition(json);

                if (indexDefinition?.Name == null || indexDefinition.Maps.Count == 0)
                    throw new BadRequestException("Index definition must contain name and at least one map.");

                var changed = Database.IndexStore.HasChanged(indexDefinition);

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("Changed");
                    writer.WriteBool(changed);
                    writer.WriteEndObject();
                }
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/debug", "GET", AuthorizationStatus.ValidUser)]
        public Task Debug()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            var index = Database.IndexStore.GetIndex(name);
            if (index == null)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            var operation = GetStringQueryString("op");

            using (ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                if (string.Equals(operation, "map-reduce-tree", StringComparison.OrdinalIgnoreCase))
                {
                    if (index.Type.IsMapReduce() == false)
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;

                        context.Write(writer, new DynamicJsonValue
                        {
                            ["Error"] = $"{index.Name} is not map-reduce index"
                        });

                        return Task.CompletedTask;
                    }

                    var docIds = GetStringValuesQueryString("docId", required: false);

                    using (index.GetReduceTree(docIds.ToArray(), out IEnumerable<ReduceTree> trees))
                    {
                        writer.WriteReduceTrees(trees);
                    }

                    return Task.CompletedTask;
                }

                if (string.Equals(operation, "source-doc-ids", StringComparison.OrdinalIgnoreCase))
                {
                    using (index.GetIdentifiersOfMappedDocuments(GetStringQueryString("startsWith", required: false), GetStart(), GetPageSize(), out IEnumerable<string> ids))
                    {
                        writer.WriteArrayOfResultsAndCount(ids);
                    }

                    return Task.CompletedTask;
                }

                if (string.Equals(operation, "entries-fields", StringComparison.OrdinalIgnoreCase))
                {
                    var fields = index.GetEntriesFields();

                    writer.WriteStartObject();
                    writer.WriteArray("Results", fields);
                    writer.WriteEndObject();

                    return Task.CompletedTask;
                }

                throw new NotSupportedException($"{operation} is not supported");
            }
        }

        [RavenAction("/databases/*/indexes", "GET", AuthorizationStatus.ValidUser, IsDebugInformationEndpoint = true)]
        public Task GetAll()
        {
            var name = GetStringQueryString("name", required: false);

            var start = GetStart();
            var pageSize = GetPageSize();
            var namesOnly = GetBoolValueQueryString("namesOnly", required: false) ?? false;

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                IndexDefinition[] indexDefinitions;
                if (string.IsNullOrEmpty(name))
                    indexDefinitions = Database.IndexStore
                        .GetIndexes()
                        .OrderBy(x => x.Name)
                        .Skip(start)
                        .Take(pageSize)
                        .Select(x => x.GetIndexDefinition())
                        .ToArray();
                else
                {
                    var index = Database.IndexStore.GetIndex(name);
                    if (index == null)
                    {
                        HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        return Task.CompletedTask;
                    }

                    indexDefinitions = new[] { index.GetIndexDefinition() };
                }

                writer.WriteStartObject();

                writer.WriteArray(context, "Results", indexDefinitions, (w, c, indexDefinition) =>
                {
                    if (namesOnly)
                    {
                        w.WriteString(indexDefinition.Name);
                        return;
                    }

                    w.WriteIndexDefinition(c, indexDefinition);
                });

                writer.WriteEndObject();
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/stats", "GET", AuthorizationStatus.ValidUser, IsDebugInformationEndpoint = true)]
        public Task Stats()
        {
            var name = GetStringQueryString("name", required: false);

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                IndexStats[] indexStats;
                using (context.OpenReadTransaction())
                {
                    if (string.IsNullOrEmpty(name))
                    {
                        indexStats = Database.IndexStore
                            .GetIndexes()
                            .OrderBy(x => x.Name)
                            .Select(x =>
                            {
                                try
                                {
                                    return x.GetStats(calculateLag: true, calculateStaleness: true, documentsContext: context);
                                }
                                catch (Exception e)
                                {
                                    if (Logger.IsOperationsEnabled)
                                        Logger.Operations($"Failed to get stats of '{x.Name}' index", e);

                                    try
                                    {
                                        Database.NotificationCenter.Add(AlertRaised.Create(Database.Name, $"Failed to get stats of '{x.Name}' index",
                                            $"Exception was thrown on getting stats of '{x.Name}' index",
                                            AlertType.Indexing_CouldNotGetStats, NotificationSeverity.Error, key: x.Name, details: new ExceptionDetails(e)));
                                    }
                                    catch (Exception addAlertException)
                                    {
                                        if (Logger.IsOperationsEnabled && addAlertException.IsOutOfMemory() == false && addAlertException.IsDiskFullException() == false)
                                            Logger.Operations($"Failed to add alert when getting error on retrieving stats of '{x.Name}' index", addAlertException);
                                    }

                                    var state = x.State;

                                    if (e.IsOutOfMemory() == false && e.IsDiskFullException() == false)
                                    {
                                        try
                                        {
                                            state = IndexState.Error;
                                            x.SetState(state, inMemoryOnly: true);
                                        }
                                        catch (Exception ex)
                                        {
                                            if (Logger.IsOperationsEnabled)
                                                Logger.Operations($"Failed to change state of '{x.Name}' index to error after encountering exception when getting its stats.",
                                                    ex);
                                        }
                                    }
                                    
                                    return new IndexStats
                                    {
                                        Name = x.Name,
                                        Type = x.Type,
                                        State = state,
                                        Status = x.Status,
                                        LockMode = x.Definition.LockMode,
                                        Priority = x.Definition.Priority,
                                    };
                                }
                            })
                            .ToArray();
                    }
                    else
                    {
                        var index = Database.IndexStore.GetIndex(name);
                        if (index == null)
                        {
                            HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            return Task.CompletedTask;
                        }

                        indexStats = new[] {index.GetStats(calculateLag: true, calculateStaleness: true, documentsContext: context)};
                    }
                }

                writer.WriteStartObject();

                writer.WriteArray(context, "Results", indexStats, (w, c, stats) =>
                {
                    w.WriteIndexStats(context, stats);
                });

                writer.WriteEndObject();
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/staleness", "GET", AuthorizationStatus.ValidUser)]
        public Task Stale()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            using (context.OpenReadTransaction())
            {
                var index = Database.IndexStore.GetIndex(name);
                if (index == null)
                    IndexDoesNotExistException.ThrowFor(name);

                var stalenessReasons = new List<string>();
                var isStale = index.IsStale(context, stalenessReasons: stalenessReasons);

                writer.WriteStartObject();

                writer.WritePropertyName("IsStale");
                writer.WriteBool(isStale);
                writer.WriteComma();

                writer.WriteArray("StalenessReasons", stalenessReasons);

                writer.WriteEndObject();
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/progress", "GET", AuthorizationStatus.ValidUser)]
        public Task Progress()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            using (context.OpenReadTransaction())
            {
                writer.WriteStartObject();
                writer.WritePropertyName("Results");
                writer.WriteStartArray();

                var first = true;
                foreach (var index in Database.IndexStore.GetIndexes())
                {
                    try
                    {
                        if (index.IsStale(context) == false)
                            continue;

                        if (first == false)
                            writer.WriteComma();

                        first = false;

                        var progress = index.GetProgress(context, isStale: true);
                        writer.WriteIndexProgress(context, progress);
                    }
                    catch (ObjectDisposedException)
                    {
                        // index was deleted?
                    }
                    catch (Exception e)
                    {
                        if (Logger.IsOperationsEnabled)
                            Logger.Operations($"Failed to get index progress for index name: {index.Name}", e);
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes", "RESET", AuthorizationStatus.ValidUser)]
        public Task Reset()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            IndexDefinition indexDefinition;
            lock (Database)
            {
                var index = Database.IndexStore.ResetIndex(name);
                indexDefinition = index.GetIndexDefinition();
            }

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("Index");
                writer.WriteIndexDefinition(context, indexDefinition);
                writer.WriteEndObject();
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/index/open-faulty-index", "POST", AuthorizationStatus.ValidUser)]
        public Task OpenFaultyIndex()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            var index = Database.IndexStore.GetIndex(name);
            if (index == null)
                IndexDoesNotExistException.ThrowFor(name);

            if (index is FaultyInMemoryIndex == false)
                throw new InvalidOperationException($"Cannot open non faulty index named: {name}");

            lock (index)
            {
                var localIndex = Database.IndexStore.GetIndex(name);
                if (localIndex == null)
                    IndexDoesNotExistException.ThrowFor(name);

                if (localIndex is FaultyInMemoryIndex == false)
                    throw new InvalidOperationException($"Cannot open non faulty index named: {name}");

                Database.IndexStore.OpenFaultyIndex(localIndex);
            }

            return NoContent();
        }

        [RavenAction("/databases/*/indexes", "DELETE", AuthorizationStatus.ValidUser)]
        public async Task Delete()
        {
            var name = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

            if (LoggingSource.AuditLog.IsInfoEnabled)
            {
                var clientCert = GetCurrentCertificate();

                var auditLog = LoggingSource.AuditLog.GetLogger(Database.Name, "Audit");
                auditLog.Info($"Index {name} DELETE by {clientCert?.Subject} {clientCert?.Thumbprint}");
            }

            HttpContext.Response.StatusCode = await Database.IndexStore.TryDeleteIndexIfExists(name)
                ? (int)HttpStatusCode.NoContent
                : (int)HttpStatusCode.NotFound;
        }

        [RavenAction("/databases/*/indexes/c-sharp-index-definition", "GET", AuthorizationStatus.ValidUser)]
        public Task GenerateCSharpIndexDefinition()
        {
            var indexName = GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");
            var index = Database.IndexStore.GetIndex(indexName);
            if (index == null)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return Task.CompletedTask;
            }

            if (index.Type.IsAuto())
                throw new InvalidOperationException("Can't create C# index definition from auto indexes");

            var indexDefinition = index.GetIndexDefinition();

            using (var writer = new StreamWriter(ResponseBodyStream()))
            {
                var text = new IndexDefinitionCodeGenerator(indexDefinition).Generate();
                writer.Write(text);
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/status", "GET", AuthorizationStatus.ValidUser)]
        public Task Status()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();

                writer.WritePropertyName(nameof(IndexingStatus.Status));
                writer.WriteString(Database.IndexStore.Status.ToString());
                writer.WriteComma();

                writer.WritePropertyName(nameof(IndexingStatus.Indexes));
                writer.WriteStartArray();
                var isFirst = true;
                foreach (var index in Database.IndexStore.GetIndexes())
                {
                    if (isFirst == false)
                        writer.WriteComma();

                    isFirst = false;

                    writer.WriteStartObject();

                    writer.WritePropertyName(nameof(IndexingStatus.IndexStatus.Name));
                    writer.WriteString(index.Name);

                    writer.WriteComma();

                    writer.WritePropertyName(nameof(IndexingStatus.IndexStatus.Status));
                    writer.WriteString(index.Status.ToString());

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/set-lock", "POST", AuthorizationStatus.ValidUser)]
        public async Task SetLockMode()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "index/set-lock");
                var parameters = JsonDeserializationServer.Parameters.SetIndexLockParameters(json);

                if (parameters.IndexNames == null || parameters.IndexNames.Length == 0)
                    throw new ArgumentNullException(nameof(parameters.IndexNames));

                // Check for auto-indexes - we do not set lock for auto-indexes
                if (parameters.IndexNames.Any(indexName => indexName.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("'Indexes list contains Auto-Indexes. Lock Mode' is not set for Auto-Indexes.");
                }

                foreach (var name in parameters.IndexNames)
                {
                    await Database.IndexStore.SetLock(name, parameters.Mode);
                }
            }

            NoContentStatus();
        }

        [RavenAction("/databases/*/indexes/set-priority", "POST", AuthorizationStatus.ValidUser)]
        public async Task SetPriority()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var json = await context.ReadForMemoryAsync(RequestBodyStream(), "index/set-priority");
                var parameters = JsonDeserializationServer.Parameters.SetIndexPriorityParameters(json);

                foreach (var name in parameters.IndexNames)
                {
                    await Database.IndexStore.SetPriority(name, parameters.Priority);
                }

                NoContentStatus();
            }
        }

        [RavenAction("/databases/*/indexes/errors", "GET", AuthorizationStatus.ValidUser, IsDebugInformationEndpoint = true)]
        public Task GetErrors()
        {
            var names = GetStringValuesQueryString("name", required: false);

            List<Index> indexes;
            if (names.Count == 0)
                indexes = Database.IndexStore.GetIndexes().ToList();
            else
            {
                indexes = new List<Index>();
                foreach (var name in names)
                {
                    var index = Database.IndexStore.GetIndex(name);
                    if (index == null)
                        IndexDoesNotExistException.ThrowFor(name);

                    indexes.Add(index);
                }
            }

            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WriteStartObject();
                writer.WriteArray(context, "Results", indexes, (w, c, index) =>
                {
                    w.WriteStartObject();
                    w.WritePropertyName("Name");
                    w.WriteString(index.Name);
                    w.WriteComma();
                    w.WriteArray(c, "Errors", index.GetErrors(), (ew, ec, error) =>
                    {
                        ew.WriteStartObject();
                        ew.WritePropertyName(nameof(error.Timestamp));
                        ew.WriteDateTime(error.Timestamp, isUtc: true);
                        ew.WriteComma();

                        ew.WritePropertyName(nameof(error.Document));
                        ew.WriteString(error.Document);
                        ew.WriteComma();

                        ew.WritePropertyName(nameof(error.Action));
                        ew.WriteString(error.Action);
                        ew.WriteComma();

                        ew.WritePropertyName(nameof(error.Error));
                        ew.WriteString(error.Error);
                        ew.WriteEndObject();
                    });
                    w.WriteEndObject();
                });
                writer.WriteEndObject();
            }
            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/terms", "GET", AuthorizationStatus.ValidUser)]
        public Task Terms()
        {
            var field = GetQueryStringValueAndAssertIfSingleAndNotEmpty("field");

            using (var token = CreateTimeLimitedOperationToken())
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (context.OpenReadTransaction())
            {
                var name = GetIndexNameFromCollectionAndField(field, context) ?? GetQueryStringValueAndAssertIfSingleAndNotEmpty("name");

                var fromValue = GetStringQueryString("fromValue", required: false);
                var existingResultEtag = GetLongFromHeaders("If-None-Match");

                var result = Database.QueryRunner.ExecuteGetTermsQuery(name, field, fromValue, existingResultEtag, GetPageSize(), context, token, out var index);

                if (result.NotModified)
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotModified;
                    return Task.CompletedTask;
                }

                HttpContext.Response.Headers[Constants.Headers.Etag] = CharExtensions.ToInvariantString(result.ResultEtag);

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    if (field.EndsWith("__minX") ||
                        field.EndsWith("__minY") ||
                        field.EndsWith("__maxX") ||
                        field.EndsWith("__maxY"))
                    {
                        if (index.Definition.IndexFields != null &&
                            index.Definition.IndexFields.TryGetValue(field.Substring(0, field.Length - 6), out var indexField) == true)
                        {
                            if (indexField.Spatial?.Strategy == Client.Documents.Indexes.Spatial.SpatialSearchStrategy.BoundingBox)
                            {
                                // Term-values for 'Spatial Index Fields' with 'BoundingBox' are encoded in Lucene as 'prefixCoded bytes'
                                // Need to convert to numbers for the Studio
                                var readableTerms = new HashSet<string>();
                                foreach (var item in result.Terms)
                                {
                                    var num = Lucene.Net.Util.NumericUtils.PrefixCodedToDouble(item);
                                    readableTerms.Add(NumberUtil.NumberToString(num));
                                }

                                result.Terms = readableTerms;
                            }
                        }
                    }

                    writer.WriteTermsQueryResult(context, result);
                }

                return Task.CompletedTask;
            }
        }

        private string GetIndexNameFromCollectionAndField(string field, DocumentsOperationContext context)
        {
            var collection = GetStringQueryString("collection", false);
            if (string.IsNullOrEmpty(collection))
                return null;
            var query = new IndexQueryServerSide(new QueryMetadata($"from {collection} select {field}", null, 0));
            var dynamicQueryToIndex = new DynamicQueryToIndexMatcher(Database.IndexStore);
            var match = dynamicQueryToIndex.Match(DynamicQueryMapping.Create(query), context);
            if (match.MatchType == DynamicQueryMatchType.Complete ||
                match.MatchType == DynamicQueryMatchType.CompleteButIdle)
                return match.IndexName;
            throw new IndexDoesNotExistException($"There is no index to answer the following query: from {collection} select {field}");
        }

        [RavenAction("/databases/*/indexes/total-time", "GET", AuthorizationStatus.ValidUser)]
        public Task TotalTime()
        {
            var indexes = GetIndexesToReportOn();
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                var dja = new DynamicJsonArray();

                foreach (var index in indexes)
                {
                    DateTime baseLine = DateTime.MinValue;
                    using (context.OpenReadTransaction())
                    {
                        foreach (var collection in index.Collections)
                        {
                            var etag = Database.DocumentsStorage.GetLastDocumentEtag(context, collection);
                            var document = Database.DocumentsStorage.GetDocumentsFrom(context, collection, etag, 0, 1).FirstOrDefault();
                            if (document != null)
                            {
                                if (document.LastModified > baseLine)
                                    baseLine = document.LastModified;
                            }
                        }
                    }
                    var createdTimestamp = index.GetStats().CreatedTimestamp;
                    if (createdTimestamp > baseLine)
                        baseLine = createdTimestamp;

                    var lastBatch = index.GetIndexingPerformance()
                                    .LastOrDefault(x => x.Completed != null)
                                    ?.Completed ?? DateTime.UtcNow;


                    dja.Add(new DynamicJsonValue
                    {
                        ["Name"] = index.Name,
                        ["TotalIndexingTime"] = index.TimeSpentIndexing.Elapsed.ToString("c"),
                        ["LagTime"] = (lastBatch - baseLine).ToString("c")
                    });
                }

                context.Write(writer, dja);
            }
            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/performance", "GET", AuthorizationStatus.ValidUser)]
        public Task Performance()
        {
            var stats = GetIndexesToReportOn()
                .Select(x => new IndexPerformanceStats
                {
                    Name = x.Name,
                    Performance = x.GetIndexingPerformance()
                })
                .ToArray();

            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                writer.WritePerformanceStats(context, stats);
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/indexes/performance/live", "GET", AuthorizationStatus.ValidUser, SkipUsagesCount = true)]
        public async Task PerformanceLive()
        {
            using (var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync())
            {
                var indexes = GetIndexesToReportOn().ToArray();

                var receiveBuffer = new ArraySegment<byte>(new byte[1024]);
                var receive = webSocket.ReceiveAsync(receiveBuffer, Database.DatabaseShutdown);

                using (var ms = new MemoryStream())
                using (var collector = new LiveIndexingPerformanceCollector(Database, Database.DatabaseShutdown, indexes))
                {
                    // 1. Send data to webSocket without making UI wait upon opening webSocket
                    await SendDataOrHeartbeatToWebSocket(receive, webSocket, collector, ms, 100);

                    // 2. Send data to webSocket when available
                    while (Database.DatabaseShutdown.IsCancellationRequested == false)
                    {
                        if (await SendDataOrHeartbeatToWebSocket(receive, webSocket, collector, ms, 4000) == false)
                        {
                            break;
                        }
                    }
                }
            }
        }

        [RavenAction("/databases/*/indexes/suggest-index-merge", "GET", AuthorizationStatus.ValidUser)]
        public Task SuggestIndexMerge()
        {
            var mergeIndexSuggestions = Database.IndexStore.ProposeIndexMergeSuggestions();

            HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
            {
                context.Write(writer, mergeIndexSuggestions.ToJson());
                writer.Flush();
            }
            return Task.CompletedTask;

        }

        [RavenAction("/databases/*/indexes/try", "POST", AuthorizationStatus.ValidUser)]
        public async Task TestJavaScriptIndex()
        {
            using (ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var input = await context.ReadForMemoryAsync(RequestBodyStream(), "TestJavaScriptIndex");
                if (input.TryGet("Definition", out BlittableJsonReaderObject index) == false)
                    ThrowRequiredPropertyNameInRequest("Definition");

                input.TryGet("Ids", out BlittableJsonReaderArray ids);

                var indexDefinition = JsonDeserializationServer.IndexDefinition(index);

                if (indexDefinition.Maps == null || indexDefinition.Maps.Count == 0)
                    throw new ArgumentException("Index must have a 'Maps' fields");

                indexDefinition.Type = indexDefinition.DetectStaticIndexType();

                if (indexDefinition.Type.IsJavaScript() == false)
                    throw new UnauthorizedAccessException("Testing indexes is only allowed for JavaScript indexes.");

                var compiledIndex = new JavaScriptIndex(indexDefinition, Database.Configuration);

                var inputSize = GetIntValueQueryString("inputSize", false) ?? defaultInputSizeForTestingJavaScriptIndex;
                var collections = new HashSet<string>(compiledIndex.Maps.Keys);
                var docsPerCollection = new Dictionary<string, List<DynamicBlittableJson>>();
                using (context.OpenReadTransaction())
                {
                    if (ids == null)
                    {
                        foreach (var collection in collections)
                        {
                            docsPerCollection.Add(collection,
                                Database.DocumentsStorage.GetDocumentsFrom(context, collection, 0, 0, inputSize).Select(d => new DynamicBlittableJson(d)).ToList());
                        }
                    }
                    else
                    {
                        var listOfIds = ids.Select(x => x.ToString());
                        var _ = new Reference<int>
                        {
                            Value = 0
                        };
                        var docs = Database.DocumentsStorage.GetDocuments(context, listOfIds, 0, int.MaxValue, _);
                        foreach (var doc in docs)
                        {
                            if (doc.TryGetMetadata(out var metadata) && metadata.TryGet(Constants.Documents.Metadata.Collection, out string collectionStr))
                            {
                                if (docsPerCollection.TryGetValue(collectionStr, out var listOfDocs) == false)
                                {
                                    listOfDocs = docsPerCollection[collectionStr] = new List<DynamicBlittableJson>();
                                }
                                listOfDocs.Add(new DynamicBlittableJson(doc));
                            }                            
                        }
                    }

                    var mapRes = new List<ObjectInstance>();
                    //all maps
                    foreach (var ListOfFunctions in compiledIndex.Maps)
                    {
                        //multi maps per collection
                        foreach (var mapFunc in ListOfFunctions.Value)
                        {
                            if (docsPerCollection.TryGetValue(ListOfFunctions.Key, out var docs))
                            {
                                foreach (var res in mapFunc(docs))
                                {
                                    mapRes.Add((ObjectInstance)res);
                                }
                            }                                                                                 
                        }
                    }
                    var first = true;
                    using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                    {

                            writer.WriteStartObject();
                            writer.WritePropertyName("MapResults");
                            writer.WriteStartArray();
                            foreach (var mapResult in mapRes)
                            {
                                if (JavaScriptIndexUtils.StringifyObject(mapResult) is JsString jsStr)
                                {
                                    if (first == false)
                                    {
                                        writer.WriteComma();
                                    }
                                    writer.WriteString(jsStr.ToString());
                                    first = false;
                                }
                                
                            }
                            writer.WriteEndArray();
                        if (indexDefinition.Reduce != null)
                        {
                            using (var bufferPool = new UnmanagedBuffersPoolWithLowMemoryHandling("JavaScriptIndexTest", Database.Name))
                            {
                                compiledIndex.SetBufferPoolForTestingPurposes(bufferPool);
                                compiledIndex.SetAllocatorForTestingPurposes(context.Allocator);
                                first = true;
                                writer.WritePropertyName("ReduceResults");
                                writer.WriteStartArray();
                                
                                var reduceResults = compiledIndex.Reduce(mapRes.Select(mr => new DynamicBlittableJson(JsBlittableBridge.Translate(context, mr.Engine,mr))));
                                
                                foreach (JsValue reduceResult in reduceResults)
                                {
                                    if (JavaScriptIndexUtils.StringifyObject(reduceResult) is JsString jsStr)
                                    {
                                        if (first == false)
                                        {
                                            writer.WriteComma();
                                        }

                                        writer.WriteString(jsStr.ToString());
                                        first = false;
                                    }

                                }
                            }

                            writer.WriteEndArray();
                        }
                        writer.WriteEndObject();
                    }                    

                }
            }
        }

        private static readonly int defaultInputSizeForTestingJavaScriptIndex = 10;

        private async Task<bool> SendDataOrHeartbeatToWebSocket(Task<WebSocketReceiveResult> receive, WebSocket webSocket, LiveIndexingPerformanceCollector collector, MemoryStream ms, int timeToWait)
        {
            if (receive.IsCompleted || webSocket.State != WebSocketState.Open)
                return false;

            var tuple = await collector.Stats.TryDequeueAsync(TimeSpan.FromMilliseconds(timeToWait));
            if (tuple.Item1 == false)
            {
                await webSocket.SendAsync(WebSocketHelper.Heartbeat, WebSocketMessageType.Text, true, Database.DatabaseShutdown);
                return true;
            }

            ms.SetLength(0);

            using (ContextPool.AllocateOperationContext(out JsonOperationContext context))
            using (var writer = new BlittableJsonTextWriter(context, ms))
            {
                writer.WritePerformanceStats(context, tuple.Item2);
            }

            ms.TryGetBuffer(out ArraySegment<byte> bytes);
            await webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, Database.DatabaseShutdown);

            return true;
        }

        private IEnumerable<Index> GetIndexesToReportOn()
        {
            IEnumerable<Index> indexes;
            var names = HttpContext.Request.Query["name"];

            if (names.Count == 0)
                indexes = Database.IndexStore
                    .GetIndexes();
            else
            {
                indexes = Database.IndexStore
                    .GetIndexes()
                    .Where(x => names.Contains(x.Name, StringComparer.OrdinalIgnoreCase));
            }
            return indexes;
        }
    }
}
