﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using Raven.Client.Documents.Conventions;
using Raven.Client.Documents.Session;
using Raven.Client.Http;
using Raven.Client.Json;
using Raven.Client.Json.Converters;
using Sparrow.Json;

namespace Raven.Client.Documents.Commands.Batches
{
    public class BatchCommand : RavenCommand<BatchCommandResult>, IDisposable
    {
        private readonly BlittableJsonReaderObject[] _commands;
        private readonly List<Stream> _attachmentStreams;
        private readonly HashSet<Stream> _uniqueAttachmentStreams;
        private readonly BatchOptions _options;
        private readonly TransactionMode _mode;

        public BatchCommand(DocumentConventions conventions, JsonOperationContext context, List<ICommandData> commands, BatchOptions options = null, TransactionMode mode = TransactionMode.SingleNode)
        {
            if (conventions == null)
                throw new ArgumentNullException(nameof(conventions));
            if (commands == null)
                throw new ArgumentNullException(nameof(commands));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            _commands = new BlittableJsonReaderObject[commands.Count];
            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                var json = command.ToJson(conventions, context);
                _commands[i] = context.ReadObject(json, "command");

                if (command is PutAttachmentCommandData putAttachmentCommandData)
                {
                    if (_attachmentStreams == null)
                    {
                        _attachmentStreams = new List<Stream>();
                        _uniqueAttachmentStreams = new HashSet<Stream>();
                    }

                    var stream = putAttachmentCommandData.Stream;
                    PutAttachmentCommandHelper.ValidateStream(stream);
                    if (_uniqueAttachmentStreams.Add(stream) == false)
                        PutAttachmentCommandHelper.ThrowStreamWasAlreadyUsed();
                    _attachmentStreams.Add(stream);
                }
            }

            _options = options;
            _mode = mode;

            Timeout = options?.RequestTimeout;
        }

        public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
        {
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                Content = new BlittableJsonContent(stream =>
                {
                    using (var writer = new BlittableJsonTextWriter(ctx, stream))
                    {
                        writer.WriteStartObject();
                        writer.WriteArray("Commands", _commands);
                        if (_mode == TransactionMode.ClusterWide)
                        {
                            writer.WriteComma();
                            writer.WritePropertyName(nameof(TransactionMode));
                            writer.WriteString(nameof(TransactionMode.ClusterWide));
                        }
                        writer.WriteEndObject();
                    }
                })
            };

            if (_attachmentStreams != null && _attachmentStreams.Count > 0)
            {
                var multipartContent = new MultipartContent {request.Content};
                foreach (var stream in _attachmentStreams)
                {
                    PutAttachmentCommandHelper.PrepareStream(stream);
                    var streamContent = new AttachmentStreamContent(stream, CancellationToken);
                    streamContent.Headers.TryAddWithoutValidation("Command-Type", "AttachmentStream");
                    multipartContent.Add(streamContent);
                }
                request.Content = multipartContent;
            }

            var sb = new StringBuilder($"{node.Url}/databases/{node.Database}/bulk_docs");

            AppendOptions(sb);

            url = sb.ToString();

            return request;
        }

        public override void SetResponse(JsonOperationContext context, BlittableJsonReaderObject response, bool fromCache)
        {
            if (response == null)
                throw new InvalidOperationException("Got null response from the server after doing a batch, something is very wrong. Probably a garbled response.");
            // this should never actually occur, we are not caching the response of batch commands, but keeping it here anyway
            if (fromCache)
            {
                // we have to clone the response here because  otherwise the cached item might be freed while
                // we are still looking at this result, so we clone it to the side
                response = response.Clone(context);
            }
            Result = JsonDeserializationClient.BatchCommandResult(response);
        }

        private void AppendOptions(StringBuilder sb)
        {
            if (_options == null)
                return;

            sb.AppendLine("?");

            var replicationOptions = _options.ReplicationOptions;
            if (replicationOptions != null)
            {
                sb.Append("&waitForReplicasTimeout=").Append(replicationOptions.WaitForReplicasTimeout);

                if (replicationOptions.ThrowOnTimeoutInWaitForReplicas)
                    sb.Append("&throwOnTimeoutInWaitForReplicas=true");

                sb.Append("&numberOfReplicasToWaitFor=");
                sb.Append(replicationOptions.Majority
                    ? "majority"
                    : replicationOptions.NumberOfReplicasToWaitFor.ToString());
            }

            var indexOptions = _options.IndexOptions;
            if (indexOptions != null)
            {
                sb.Append("&waitForIndexesTimeout=").Append(indexOptions.WaitForIndexesTimeout);
                sb.Append("&waitForIndexThrow=").Append(indexOptions.ThrowOnTimeoutInWaitForIndexes.ToString());
                if (indexOptions.WaitForSpecificIndexes != null)
                {
                    foreach (var specificIndex in indexOptions.WaitForSpecificIndexes)
                    {
                        sb.Append("&waitForSpecificIndex=").Append(specificIndex);
                    }
                }
            }
        }

        public override bool IsReadRequest => false;

        public void Dispose()
        {
            foreach (var command in _commands)
                command?.Dispose();
        }
    }
}
