﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Raven.Client.Documents.Changes;
using Raven.Client.Exceptions.Documents;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Server.Documents.Queries;
using Raven.Server.Json;
using Raven.Server.NotificationCenter;
using Raven.Server.Routing;
using Raven.Server.ServerWide.Context;
using Raven.Server.TrafficWatch;
using Sparrow.Json;

namespace Raven.Server.Documents.Handlers
{
    public class StreamingHandler : DatabaseRequestHandler
    {
        [RavenAction("/databases/*/streams/docs", "GET", AuthorizationStatus.ValidUser)]
        public Task StreamDocsGet()
        {
            var start = GetStart();
            var pageSize = GetPageSize();

            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            using (context.OpenReadTransaction())
            {
                IEnumerable<Document> documents;
                if (HttpContext.Request.Query.ContainsKey("startsWith"))
                {
                    documents = Database.DocumentsStorage.GetDocumentsStartingWith(context,
                        HttpContext.Request.Query["startsWith"],
                        HttpContext.Request.Query["matches"],
                        HttpContext.Request.Query["excludes"],
                        HttpContext.Request.Query["startAfter"],
                        start,
                        pageSize);
                }
                else // recent docs
                {
                    documents = Database.DocumentsStorage.GetDocumentsInReverseEtagOrder(context, start, pageSize);
                }

                using (var writer = new BlittableJsonTextWriter(context, ResponseBodyStream()))
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("Results");

                    writer.WriteDocuments(context, documents, metadataOnly: false, numberOfResults: out int _);

                    writer.WriteEndObject();
                }
            }

            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/streams/queries", "HEAD", AuthorizationStatus.ValidUser)]
        public Task SteamQueryHead()
        {
            return Task.CompletedTask;
        }

        [RavenAction("/databases/*/streams/queries", "GET", AuthorizationStatus.ValidUser)]
        public async Task StreamQueryGet()
        {
            // ReSharper disable once ArgumentsStyleLiteral
            using (var tracker = new RequestTimeTracker(HttpContext, Logger, Database, "StreamQuery", doPerformanceHintIfTooLong: false))
            using (var token = CreateTimeLimitedQueryToken())
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var documentId = GetStringQueryString("fromDocument", false);
                string overrideQuery = null;
                if (string.IsNullOrEmpty(documentId) == false)
                {
                    Document document;
                    using (context.OpenReadTransaction())
                    {
                        document = Database.DocumentsStorage.Get(context, documentId);                    
                        if (document == null)
                        {
                            throw new DocumentDoesNotExistException($"Was request to stream a query taken from {documentId} document, but it does not exist.");
                        }
                        if (document.Data.TryGet("Query", out overrideQuery) == false)
                        {
                            throw new MissingFieldException($"Expected {documentId} to have a property named 'Query' of type 'String' but couldn't locate such property.");
                        }
                    }
                }
                var query = IndexQueryServerSide.Create(HttpContext, GetStart(), GetPageSize(), context, tracker, overrideQuery);
                var format = GetStringQueryString("format", false);
                var debug = GetStringQueryString("debug", false);
                var properties = GetStringValuesQueryString("field", false);
                var propertiesArray = properties.Count == 0 ? null : properties.ToArray();
                // set the exported file name prefix
                var fileNamePrefix = query.Metadata.IsCollectionQuery ? query.Metadata.CollectionName + "_collection" : "query_result";
                fileNamePrefix = $"{Database.Name}_{fileNamePrefix}";
                if (string.IsNullOrWhiteSpace(debug) == false)
                {
                    if (string.Equals(debug, "entries", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var writer = GetIndexEntriesQueryResultWriter(format, HttpContext.Response, context, ResponseBodyStream(), propertiesArray, fileNamePrefix))
                        {
                            try
                            {
                                await Database.QueryRunner.ExecuteStreamIndexEntriesQuery(query, context, HttpContext.Response, writer, token).ConfigureAwait(false);
                            }
                            catch (IndexDoesNotExistException)
                            {
                                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                writer.WriteError($"Index {query.Metadata.IndexName} does not exist");
                            }
                        }
                    }
                    else
                    {
                        ThrowUnsupportedException($"You have selected {debug} debug mode, which is not supported.");
                    }
                }
                else
                {
                    using (var writer = GetQueryResultWriter(format, HttpContext.Response, context, ResponseBodyStream(), propertiesArray, fileNamePrefix))
                    {
                        try
                        {
                            await Database.QueryRunner.ExecuteStreamQuery(query, context, HttpContext.Response, writer, token).ConfigureAwait(false);
                        }
                        catch (IndexDoesNotExistException)
                        {
                            HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            writer.WriteError($"Index {query.Metadata.IndexName} does not exist");
                        }
                    }
                }
            }
        }

        [RavenAction("/databases/*/streams/queries", "POST", AuthorizationStatus.ValidUser)]
        public async Task StreamQueryPost()
        {
            // ReSharper disable once ArgumentsStyleLiteral
            using (var tracker = new RequestTimeTracker(HttpContext, Logger, Database, "StreamQuery", doPerformanceHintIfTooLong: false))
            using (var token = CreateTimeLimitedQueryToken())
            using (Database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext context))
            {
                var stream = TryGetRequestFromStream("ExportOptions") ?? RequestBodyStream();
                var queryJson = await context.ReadForMemoryAsync(stream, "index/query");
                var query = IndexQueryServerSide.Create(HttpContext, queryJson, Database.QueryMetadataCache, tracker);

                if (TrafficWatchManager.HasRegisteredClients)
                {
                    var sb = new StringBuilder();
                    // append stringBuilder with the query
                    sb.Append(query.Query);
                    // if query got parameters append with parameters
                    if (query.QueryParameters != null && query.QueryParameters.Count > 0)
                        sb.AppendLine().Append(query.QueryParameters);
                    AddStringToHttpContext(sb.ToString(), TrafficWatchChangeType.Streams);
                }

                var format = GetStringQueryString("format", false);
                var debug = GetStringQueryString("debug", false);
                var properties = GetStringValuesQueryString("field", false);
                var propertiesArray = properties.Count == 0 ? null : properties.ToArray();
                
                // set the exported file name prefix
                var fileNamePrefix = query.Metadata.IsCollectionQuery ? query.Metadata.CollectionName + "_collection" : "query_result";
                fileNamePrefix = $"{Database.Name}_{fileNamePrefix}";
                if (string.IsNullOrWhiteSpace(debug) == false)
                {
                    if (string.Equals(debug, "entries", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var writer = GetIndexEntriesQueryResultWriter(format, HttpContext.Response, context, ResponseBodyStream(), propertiesArray, fileNamePrefix))
                        {
                            try
                            {
                                await Database.QueryRunner.ExecuteStreamIndexEntriesQuery(query, context, HttpContext.Response, writer, token).ConfigureAwait(false);
                            }
                            catch (IndexDoesNotExistException)
                            {
                                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                                writer.WriteError($"Index {query.Metadata.IndexName} does not exist");
                            }
                        }
                    }
                    else
                    {
                        ThrowUnsupportedException($"You have selected {debug} debug mode, which is not supported.");
                    }
                }
                else
                {
                    using (var writer = GetQueryResultWriter(format, HttpContext.Response, context, ResponseBodyStream(), propertiesArray, fileNamePrefix))
                    {
                        try
                        {
                            await Database.QueryRunner.ExecuteStreamQuery(query, context, HttpContext.Response, writer, token).ConfigureAwait(false);
                        }
                        catch (IndexDoesNotExistException)
                        {
                            HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                            writer.WriteError($"Index {query.Metadata.IndexName} does not exist");
                        }
                    }
                }
            }
        }

        private StreamCsvBlittableQueryResultWriter GetIndexEntriesQueryResultWriter(string format, HttpResponse response, DocumentsOperationContext context, Stream responseBodyStream,
            string[] propertiesArray, string fileNamePrefix = null)
        {
            if (string.IsNullOrEmpty(format) || string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase) == false)
                ThrowUnsupportedException($"You have selected \"{format}\" file format, which is not supported.");

            return new StreamCsvBlittableQueryResultWriter(response, responseBodyStream, propertiesArray, fileNamePrefix);
        }

        private IStreamQueryResultWriter<Document> GetQueryResultWriter(string format, HttpResponse response, DocumentsOperationContext context, Stream responseBodyStream,
            string[] propertiesArray, string fileNamePrefix = null)
        {
            if (string.IsNullOrEmpty(format) == false && string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                return new StreamCsvDocumentQueryResultWriter(response, responseBodyStream, context, propertiesArray, fileNamePrefix);
            }

            if (propertiesArray != null)
            {
                ThrowUnsupportedException("Using json output format with custom fields is not supported.");
            }

            return new StreamJsonDocumentQueryResultWriter(response, responseBodyStream, context);
        }

        private void ThrowUnsupportedException(string message)
        {
            throw new NotSupportedException(message);
        }
    }
}
