﻿using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Documents.Replication;
using Raven.Client.Documents.Replication.Messages;
using Raven.Client.Extensions;
using Raven.Client.ServerWide.Tcp;
using Raven.Client.Util;
using Raven.Server.Documents.TcpHandlers;
using Raven.Server.Exceptions;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;
using Sparrow.Platform;
using Sparrow.Utils;
using Voron;
using Size = Sparrow.Size;

namespace Raven.Server.Documents.Replication
{
    public class IncomingReplicationHandler : IDisposable
    {
        private readonly DocumentDatabase _database;
        private readonly TcpClient _tcpClient;
        private readonly Stream _stream;
        private readonly ReplicationLoader _parent;
        private PoolOfThreads.LongRunningWork _incomingWork;
        private readonly CancellationTokenSource _cts;
        private readonly Logger _log;
        public event Action<IncomingReplicationHandler, Exception> Failed;
        public event Action<IncomingReplicationHandler> DocumentsReceived;
        public event Action<LiveReplicationPulsesCollector.ReplicationPulse> HandleReplicationPulse;

        public long LastDocumentEtag;
        public long LastHeartbeatTicks;

        private readonly ConcurrentQueue<IncomingReplicationStatsAggregator> _lastReplicationStats = new ConcurrentQueue<IncomingReplicationStatsAggregator>();

        private IncomingReplicationStatsAggregator _lastStats;

        public IncomingReplicationHandler(TcpConnectionOptions options,
            ReplicationLatestEtagRequest replicatedLastEtag,
            ReplicationLoader parent,
            JsonOperationContext.ManagedPinnedBuffer bufferToCopy)
        {
            _connectionOptions = options;
            ConnectionInfo = IncomingConnectionInfo.FromGetLatestEtag(replicatedLastEtag);

            _database = options.DocumentDatabase;
            _tcpClient = options.TcpClient;
            _stream = options.Stream;
            SupportedFeatures = TcpConnectionHeaderMessage.GetSupportedFeaturesFor(TcpConnectionHeaderMessage.OperationTypes.Replication, options.ProtocolVersion);
            ConnectionInfo.RemoteIp = ((IPEndPoint)_tcpClient.Client.RemoteEndPoint).Address.ToString();
            _parent = parent;

            _log = LoggingSource.Instance.GetLogger<IncomingReplicationHandler>(_database.Name);
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_database.DatabaseShutdown);

            _conflictManager = new ConflictManager(_database, _parent.ConflictResolver);

            _attachmentStreamsTempFile = _database.DocumentsStorage.AttachmentsStorage.GetTempFile("replication");

            _copiedBuffer = bufferToCopy.Clone(_connectionOptions.ContextPool);
        }

        public IncomingReplicationPerformanceStats[] GetReplicationPerformance()
        {
            var lastStats = _lastStats;

            return _lastReplicationStats
                .Select(x => x == lastStats ? x.ToReplicationPerformanceLiveStatsWithDetails() : x.ToReplicationPerformanceStats())
                .ToArray();
        }

        public IncomingReplicationStatsAggregator GetLatestReplicationPerformance()
        {
            return _lastStats;
        }

        private string IncomingReplicationThreadName => $"Incoming replication {FromToString}";

        public void Start()
        {
            if (_incomingWork != null)
                return;

            lock (this)
            {
                if (_incomingWork != null)
                    return; // already set by someone else, they can start it

                _incomingWork = PoolOfThreads.GlobalRavenThreadPool.LongRunning(x =>
                {
                    try
                    {
                        ReceiveReplicationBatches();
                    }
                    catch (Exception e)
                    {
                        if (_log.IsInfoEnabled)
                            _log.Info($"Error in accepting replication request ({FromToString})", e);
                    }
                }, null, IncomingReplicationThreadName);
            }

            if (_log.IsInfoEnabled)
                _log.Info($"Incoming replication thread started ({FromToString})");
        }

        [ThreadStatic]
        public static bool IsIncomingReplication;

        static IncomingReplicationHandler()
        {
            ThreadLocalCleanup.ReleaseThreadLocalState += () => IsIncomingReplication = false;
        }

        private readonly AsyncManualResetEvent _replicationFromAnotherSource = new AsyncManualResetEvent();

        public void OnReplicationFromAnotherSource()
        {
            _replicationFromAnotherSource.Set();
        }

        private void ReceiveReplicationBatches()
        {
            NativeMemory.EnsureRegistered();
            try
            {
                using (_connectionOptionsDisposable = _connectionOptions.ConnectionProcessingInProgress("Replication"))
                using (_stream)
                using (var interruptibleRead = new InterruptibleRead(_database.DocumentsStorage.ContextPool, _stream))
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        try
                        {
                            AddReplicationPulse(ReplicationPulseDirection.IncomingInitiate);

                            using (var msg = interruptibleRead.ParseToMemory(
                                _replicationFromAnotherSource,
                                "IncomingReplication/read-message",
                                Timeout.Infinite,
                                _copiedBuffer.Buffer,
                                _database.DatabaseShutdown))
                            {
                                if (msg.Document != null)
                                {
                                    _parent.EnsureNotDeleted(_parent._server.NodeTag);

                                    using (var writer = new BlittableJsonTextWriter(msg.Context, _stream))
                                    {
                                        HandleSingleReplicationBatch(msg.Context,
                                            msg.Document,
                                            writer);
                                    }
                                }
                                else // notify peer about new change vector
                                {
                                    using (_database.DocumentsStorage.ContextPool.AllocateOperationContext(
                                            out DocumentsOperationContext documentsContext))
                                    using (var writer = new BlittableJsonTextWriter(documentsContext, _stream))
                                    {
                                        SendHeartbeatStatusToSource(
                                            documentsContext,
                                            writer,
                                            _lastDocumentEtag,
                                            "Notify");
                                    }
                                }
                                // we reset it after every time we send to the remote server
                                // because that is when we know that it is up to date with our
                                // status, so no need to send again
                                _replicationFromAnotherSource.Reset();
                            }
                        }
                        catch (Exception e)
                        {
                            AddReplicationPulse(ReplicationPulseDirection.IncomingInitiateError, e.Message);

                            if (_log.IsInfoEnabled)
                            {
                                if (e is AggregateException ae &&
                                    ae.InnerExceptions.Count == 1 &&
                                    ae.InnerException is SocketException ase)
                                {
                                    HandleSocketException(ase);
                                }
                                else if (e.InnerException is SocketException se)
                                {
                                    HandleSocketException(se);
                                }
                                else
                                {
                                    //if we are disposing, do not notify about failure (not relevant)
                                    if (_cts.IsCancellationRequested == false)
                                        if (_log.IsInfoEnabled)
                                            _log.Info("Received unexpected exception while receiving replication batch.", e);
                                }
                            }

                            throw;
                        }

                        void HandleSocketException(SocketException e)
                        {
                            if (_log.IsInfoEnabled)
                                _log.Info("Failed to read data from incoming connection. The incoming connection will be closed and re-created.", e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                //if we are disposing, do not notify about failure (not relevant)
                if (_cts.IsCancellationRequested == false)
                {
                    if (_log.IsInfoEnabled)
                        _log.Info($"Connection error {FromToString}: an exception was thrown during receiving incoming document replication batch.", e);

                    OnFailed(e, this);
                }
            }
        }

        private Task _prevChangeVectorUpdate;

        private void HandleSingleReplicationBatch(
            DocumentsOperationContext documentsContext,
            BlittableJsonReaderObject message,
            BlittableJsonTextWriter writer)
        {
            message.BlittableValidation();
            //note: at this point, the valid messages are heartbeat and replication batch.
            _cts.Token.ThrowIfCancellationRequested();
            string messageType = null;
            try
            {
                if (!message.TryGet(nameof(ReplicationMessageHeader.Type), out messageType))
                    throw new InvalidDataException("Expected the message to have a 'Type' field. The property was not found");

                if (!message.TryGet(nameof(ReplicationMessageHeader.LastDocumentEtag), out _lastDocumentEtag))
                    throw new InvalidOperationException("Expected LastDocumentEtag property in the replication message, " +
                                                        "but didn't find it..");

                switch (messageType)
                {
                    case ReplicationMessageType.Documents:
                        AddReplicationPulse(ReplicationPulseDirection.IncomingBegin);

                        var stats = _lastStats = new IncomingReplicationStatsAggregator(_parent.GetNextReplicationStatsId(), _lastStats);
                        AddReplicationPerformance(stats);

                        try
                        {
                            using (var scope = stats.CreateScope())
                            {
                                try
                                {
                                    scope.RecordLastEtag(_lastDocumentEtag);

                                    HandleReceivedDocumentsAndAttachmentsBatch(documentsContext, message, _lastDocumentEtag, scope);
                                    break;
                                }
                                catch (Exception e)
                                {
                                    AddReplicationPulse(ReplicationPulseDirection.IncomingError, e.Message);
                                    scope.AddError(e);
                                    throw;
                                }
                            }
                        }
                        finally
                        {
                            AddReplicationPulse(ReplicationPulseDirection.IncomingEnd);
                            stats.Complete();
                        }
                    case ReplicationMessageType.Heartbeat:
                        AddReplicationPulse(ReplicationPulseDirection.IncomingHeartbeat);
                        if (message.TryGet(nameof(ReplicationMessageHeader.DatabaseChangeVector), out string changeVector))
                        {
                            // saving the change vector and the last received document etag
                            long lastEtag;
                            string lastChangeVector;
                            using (documentsContext.OpenReadTransaction())
                            {
                                lastEtag = DocumentsStorage.GetLastReplicatedEtagFrom(documentsContext, ConnectionInfo.SourceDatabaseId);
                                lastChangeVector = DocumentsStorage.GetDatabaseChangeVector(documentsContext);
                            }

                            var status = ChangeVectorUtils.GetConflictStatus(changeVector, lastChangeVector);
                            if (status == ConflictStatus.Update || _lastDocumentEtag > lastEtag)
                            {
                                if (_log.IsInfoEnabled)
                                {
                                    _log.Info(
                                        $"Try to update the current database change vector ({lastChangeVector}) with {changeVector} in status {status}" +
                                        $"with etag: {_lastDocumentEtag} (new) > {lastEtag} (old)");
                                }

                                var cmd = new MergedUpdateDatabaseChangeVectorCommand(changeVector, _lastDocumentEtag, ConnectionInfo.SourceDatabaseId,
                                    _replicationFromAnotherSource);
                                if (_prevChangeVectorUpdate != null && _prevChangeVectorUpdate.IsCompleted == false)
                                {
                                    if (_log.IsInfoEnabled)
                                    {
                                        _log.Info(
                                            $"The previous task of updating the database change vector was not completed and has the status of {_prevChangeVectorUpdate.Status}, " +
                                            "nevertheless we create an additional task.");
                                    }
                                }
                                else
                                {
                                    _prevChangeVectorUpdate = _database.TxMerger.Enqueue(cmd);
                                }
                            }
                        }

                        break;
                    default:
                        throw new ArgumentOutOfRangeException("Unknown message type: " + messageType);
                }

                SendHeartbeatStatusToSource(documentsContext, writer, _lastDocumentEtag, messageType);
            }
            catch (ObjectDisposedException)
            {
                //we are shutting down replication, this is ok
            }
            catch (EndOfStreamException e)
            {
                if (_log.IsInfoEnabled)
                    _log.Info("Received unexpected end of stream while receiving replication batches. " +
                              "This might indicate an issue with network.", e);
                throw;
            }
            catch (Exception e)
            {
                //if we are disposing, ignore errors
                if (_cts.IsCancellationRequested)
                    return;

                DynamicJsonValue returnValue;

                if (e.ExtractSingleInnerException() is MissingAttachmentException mae)
                {
                    returnValue = new DynamicJsonValue
                    {
                        [nameof(ReplicationMessageReply.Type)] = ReplicationMessageReply.ReplyType.MissingAttachments.ToString(),
                        [nameof(ReplicationMessageReply.MessageType)] = messageType,
                        [nameof(ReplicationMessageReply.LastEtagAccepted)] = -1,
                        [nameof(ReplicationMessageReply.Exception)] = mae.ToString()
                    };

                    documentsContext.Write(writer, returnValue);
                    writer.Flush();

                    return;
                }

                if (_log.IsInfoEnabled)
                    _log.Info($"Failed replicating documents {FromToString}.", e);

                //return negative ack
                returnValue = new DynamicJsonValue
                {
                    [nameof(ReplicationMessageReply.Type)] = ReplicationMessageReply.ReplyType.Error.ToString(),
                    [nameof(ReplicationMessageReply.MessageType)] = messageType,
                    [nameof(ReplicationMessageReply.LastEtagAccepted)] = -1,
                    [nameof(ReplicationMessageReply.Exception)] = e.ToString()
                };

                documentsContext.Write(writer, returnValue);
                writer.Flush();

                throw;
            }
        }

        private void HandleReceivedDocumentsAndAttachmentsBatch(DocumentsOperationContext documentsContext, BlittableJsonReaderObject message, long lastDocumentEtag, IncomingReplicationStatsScope stats)
        {
            if (!message.TryGet(nameof(ReplicationMessageHeader.ItemsCount), out int itemsCount))
                throw new InvalidDataException($"Expected the '{nameof(ReplicationMessageHeader.ItemsCount)}' field, " +
                                               $"but had no numeric field of this value, this is likely a bug");

            if (!message.TryGet(nameof(ReplicationMessageHeader.AttachmentStreamsCount), out int attachmentStreamCount))
                throw new InvalidDataException($"Expected the '{nameof(ReplicationMessageHeader.AttachmentStreamsCount)}' field, " +
                                               $"but had no numeric field of this value, this is likely a bug");


            ReceiveSingleDocumentsBatch(documentsContext, itemsCount, attachmentStreamCount, lastDocumentEtag, stats);

            OnDocumentsReceived(this);
        }

        private void ReadExactly(long size, Stream file)
        {
            while (size > 0)
            {
                var available = _copiedBuffer.Buffer.Valid - _copiedBuffer.Buffer.Used;
                if (available == 0)
                {
                    var read = _connectionOptions.Stream.Read(_copiedBuffer.Buffer.Buffer.Array,
                      _copiedBuffer.Buffer.Buffer.Offset,
                      _copiedBuffer.Buffer.Buffer.Count);
                    if (read == 0)
                        throw new EndOfStreamException();

                    _copiedBuffer.Buffer.Valid = read;
                    _copiedBuffer.Buffer.Used = 0;
                    continue;
                }
                var min = (int)Math.Min(size, available);
                file.Write(_copiedBuffer.Buffer.Buffer.Array,
                    _copiedBuffer.Buffer.Buffer.Offset + _copiedBuffer.Buffer.Used,
                    min);
                _copiedBuffer.Buffer.Used += min;
                size -= min;
            }
        }

        private unsafe void ReadExactly(byte* ptr, int size)
        {
            var written = 0;

            while (size > 0)
            {
                var available = _copiedBuffer.Buffer.Valid - _copiedBuffer.Buffer.Used;
                if (available == 0)
                {
                    var read = _connectionOptions.Stream.Read(_copiedBuffer.Buffer.Buffer.Array,
                      _copiedBuffer.Buffer.Buffer.Offset,
                      _copiedBuffer.Buffer.Buffer.Count);
                    if (read == 0)
                        throw new EndOfStreamException();

                    _copiedBuffer.Buffer.Valid = read;
                    _copiedBuffer.Buffer.Used = 0;
                    continue;
                }

                var min = Math.Min(size, available);
                var result = _copiedBuffer.Buffer.Pointer + _copiedBuffer.Buffer.Used;
                Memory.Copy(ptr + written, result, (uint)min);
                written += min;
                _copiedBuffer.Buffer.Used += min;
                size -= min;
            }
        }

        private unsafe byte* ReadExactly(int size)
        {
            var diff = _copiedBuffer.Buffer.Valid - _copiedBuffer.Buffer.Used;
            if (diff >= size)
            {
                var result = _copiedBuffer.Buffer.Pointer + _copiedBuffer.Buffer.Used;
                _copiedBuffer.Buffer.Used += size;
                return result;
            }
            return ReadExactlyUnlikely(size, diff);
        }

        private unsafe byte* ReadExactlyUnlikely(int size, int diff)
        {
            Memory.Move(
                _copiedBuffer.Buffer.Pointer,
                _copiedBuffer.Buffer.Pointer + _copiedBuffer.Buffer.Used,
                diff);
            _copiedBuffer.Buffer.Valid = diff;
            _copiedBuffer.Buffer.Used = 0;
            while (diff < size)
            {
                var read = _connectionOptions.Stream.Read(_copiedBuffer.Buffer.Buffer.Array,
                    _copiedBuffer.Buffer.Buffer.Offset + diff,
                    _copiedBuffer.Buffer.Buffer.Count - diff);
                if (read == 0)
                    throw new EndOfStreamException();

                _copiedBuffer.Buffer.Valid += read;
                diff += read;
            }
            var result = _copiedBuffer.Buffer.Pointer + _copiedBuffer.Buffer.Used;
            _copiedBuffer.Buffer.Used += size;
            return result;
        }

        public class DataForReplicationCommand : IDisposable
        {
            internal DocumentDatabase DocumentDatabase { get; set; }

            internal ConflictManager ConflictManager { get; set; }

            internal string SourceDatabaseId { get; set; }

            internal ArraySegment<ReplicationItem> ReplicatedItems { get; set; }

            internal ArraySegment<ReplicationAttachmentStream> AttachmentStreams { get; set; }

            internal Dictionary<Slice, ReplicationAttachmentStream> ReplicatedAttachmentStreams { get; set; }

            public TcpConnectionHeaderMessage.SupportedFeatures SupportedFeatures { get; set; }

            public Logger Logger { get; set; }

            public void Dispose()
            {
                if (ReplicatedItems.Array != null)
                {
                    foreach (var item in ReplicatedItems)
                    {
                        item.Document?.Dispose();
                    }

                    ArrayPool<ReplicationItem>.Shared.Return(ReplicatedItems.Array, clearArray: true);
                }

                if (AttachmentStreams.Array != null)
                {
                    foreach (var attachmentStream in AttachmentStreams)
                    {
                        attachmentStream.Dispose();
                    }

                    ArrayPool<ReplicationAttachmentStream>.Shared.Return(AttachmentStreams.Array, clearArray: true);
                }
            }
        }

        private void ReceiveSingleDocumentsBatch(DocumentsOperationContext documentsContext, int replicatedItemsCount, int attachmentStreamCount, long lastEtag, IncomingReplicationStatsScope stats)
        {
            if (_log.IsInfoEnabled)
            {
                _log.Info($"Receiving replication batch with {replicatedItemsCount} documents starting with {lastEtag} from {ConnectionInfo}");
            }

            var sw = Stopwatch.StartNew();
            Task task = null;

            using (var incomingReplicationAllocator = new IncomingReplicationAllocator(documentsContext, _database))
            using (var dataForReplicationCommand = new DataForReplicationCommand
            {
                DocumentDatabase = _database,
                ConflictManager = _conflictManager,
                SourceDatabaseId = ConnectionInfo.SourceDatabaseId,
                SupportedFeatures = SupportedFeatures,
                Logger = _log
            })
            {
                try
                {
                    using (var networkStats = stats.For(ReplicationOperation.Incoming.Network))
                    {
                        // this will read the documents to memory from the network
                        // without holding the write tx open
                        dataForReplicationCommand.ReplicatedItems = ReadItemsFromSource(replicatedItemsCount, documentsContext, incomingReplicationAllocator, networkStats);

                        using (networkStats.For(ReplicationOperation.Incoming.AttachmentRead))
                        {
                            ReadAttachmentStreamsFromSource(attachmentStreamCount, documentsContext, dataForReplicationCommand);
                        }
                    }

                    if (_log.IsInfoEnabled)
                    {
                        _log.Info(
                            $"Replication connection {FromToString}: " +
                            $"received {replicatedItemsCount:#,#;;0} items, " +
                            $"{attachmentStreamCount:#,#;;0} attachment streams, " +
                            $"total size: {new Size(incomingReplicationAllocator.TotalDocumentsSizeInBytes, SizeUnit.Bytes)}, " +
                            $"took: {sw.ElapsedMilliseconds:#,#;;0}ms");
                    }

                    using (stats.For(ReplicationOperation.Incoming.Storage))
                    {
                        var replicationCommand = new MergedDocumentReplicationCommand(dataForReplicationCommand, lastEtag);
                        task = _database.TxMerger.Enqueue(replicationCommand);
                        //We need a new context here
                        using(_database.DocumentsStorage.ContextPool.AllocateOperationContext(out DocumentsOperationContext msgContext))
                        using (var writer = new BlittableJsonTextWriter(msgContext, _connectionOptions.Stream))
                        using (var msg = msgContext.ReadObject(new DynamicJsonValue
                        {
                            [nameof(ReplicationMessageReply.MessageType)] = "Processing"
                        }, "heartbeat message"))
                        {
                            while (task.Wait(Math.Min(3000, (int)(_database.Configuration.Replication.ActiveConnectionTimeout.AsTimeSpan.TotalMilliseconds * 2 / 3))) ==
                                   false)
                            {
                                // send heartbeats while batch is processed in TxMerger. We wait until merger finishes with this command without timeouts
                                msgContext.Write(writer, msg);
                                writer.Flush();
                            }

                            task = null;
                        }
                    }

                    sw.Stop();
                    
                    if (_log.IsInfoEnabled)
                        _log.Info($"Replication connection {FromToString}: " +
                                  $"received and written {replicatedItemsCount:#,#;;0} items to database in {sw.ElapsedMilliseconds:#,#;;0}ms, " +
                                  $"with last etag = {lastEtag}.");
                }
                catch (Exception e)
                {
                    if (_log.IsInfoEnabled)
                    {
                        //This is the case where we had a missing attachment, it is rare but expected.
                        if (e.ExtractSingleInnerException() is MissingAttachmentException mae)
                        {
                            _log.Info("Replication batch contained missing attachments will request the batch to be re-sent with those attachments.", mae);
                        }
                        else
                        {
                            _log.Info("Failed to receive documents replication batch. This is not supposed to happen, and is likely a bug.", e);
                        }
                    }
                    throw;
                }
                finally
                {
                    // before we dispose the buffer we must ensure it is not being processed in TxMerger, so we wait for it
                    try
                    {
                        task?.Wait();
                    }
                    catch (Exception)
                    {
                        // ignore this failure, if this failed, we are already
                        // in a bad state and likely in the process of shutting 
                        // down
                    }

                    _attachmentStreamsTempFile?.Reset();
                }
            }
        }

        private void SendHeartbeatStatusToSource(DocumentsOperationContext documentsContext, BlittableJsonTextWriter writer, long lastDocumentEtag, string handledMessageType)
        {
            AddReplicationPulse(ReplicationPulseDirection.IncomingHeartbeatAcknowledge);

            string databaseChangeVector;
            long currentLastEtagMatchingChangeVector;

            using (documentsContext.OpenReadTransaction())
            {
                // we need to get both of them in a transaction, the other side will check if its known change vector
                // is the same or higher then ours, and if so, we'll update the change vector on the sibling to reflect
                // our own latest etag. This allows us to have effective synchronization points, since each change will
                // be able to tell (roughly) where it is at on the entire cluster. 
                databaseChangeVector = DocumentsStorage.GetDatabaseChangeVector(documentsContext);
                currentLastEtagMatchingChangeVector = DocumentsStorage.ReadLastEtag(documentsContext.Transaction.InnerTransaction);
            }
            if (_log.IsInfoEnabled)
            {
                _log.Info($"Sending heartbeat ok => {FromToString} with last document etag = {lastDocumentEtag}, " +
                          $"last document change vector: {databaseChangeVector}");
            }
            var heartbeat = new DynamicJsonValue
            {
                [nameof(ReplicationMessageReply.Type)] = "Ok",
                [nameof(ReplicationMessageReply.MessageType)] = handledMessageType,
                [nameof(ReplicationMessageReply.LastEtagAccepted)] = lastDocumentEtag,
                [nameof(ReplicationMessageReply.CurrentEtag)] = currentLastEtagMatchingChangeVector,
                [nameof(ReplicationMessageReply.Exception)] = null,
                [nameof(ReplicationMessageReply.DatabaseChangeVector)] = databaseChangeVector,
                [nameof(ReplicationMessageReply.DatabaseId)] = _database.DbId.ToString(),
                [nameof(ReplicationMessageReply.NodeTag)] = _parent._server.NodeTag

            };

            documentsContext.Write(writer, heartbeat);

            writer.Flush();
            LastHeartbeatTicks = _database.Time.GetUtcNow().Ticks;
        }

        public string SourceFormatted => $"{ConnectionInfo.SourceUrl}/databases/{ConnectionInfo.SourceDatabaseName} ({ConnectionInfo.SourceDatabaseId})";

        public string FromToString => $"In database {_database.ServerStore.NodeTag}-{_database.Name} @ {_database.ServerStore.GetNodeTcpServerUrl()} " +
                                      $"from {ConnectionInfo.SourceTag}-{ConnectionInfo.SourceDatabaseName} @ {ConnectionInfo.SourceUrl}";

        public IncomingConnectionInfo ConnectionInfo { get; }

        private readonly StreamsTempFile _attachmentStreamsTempFile;
        private long _lastDocumentEtag;
        private readonly TcpConnectionOptions _connectionOptions;
        private readonly ConflictManager _conflictManager;
        private IDisposable _connectionOptionsDisposable;
        private (IDisposable ReleaseBuffer, JsonOperationContext.ManagedPinnedBuffer Buffer) _copiedBuffer;
        public TcpConnectionHeaderMessage.SupportedFeatures SupportedFeatures { get; set; }

        public struct ReplicationItem : IDisposable
        {
            public short TransactionMarker;
            public ReplicationBatchItem.ReplicationItemType Type;

            #region Document

            public string Id;
            public string ChangeVector;
            public int DocumentSize;
            public string Collection;
            public long LastModifiedTicks;
            public DocumentFlags Flags;
            public BlittableJsonReaderObject Document;

            #endregion

            #region Counter

            public long CounterValue;
            public string CounterName;

            #endregion

            #region Attachment

            public Slice Key;
            public ByteStringContext.InternalScope KeyDispose;

            public Slice Name;
            public ByteStringContext.InternalScope NameDispose;

            public Slice ContentType;
            public ByteStringContext.InternalScope ContentTypeDispose;

            public Slice Base64Hash;
            public ByteStringContext.InternalScope Base64HashDispose;


            #endregion

            public void Dispose()
            {
                if (Type == ReplicationBatchItem.ReplicationItemType.Attachment)
                {
                    KeyDispose.Dispose();
                    NameDispose.Dispose();
                    ContentTypeDispose.Dispose();
                    Base64HashDispose.Dispose();
                }
                else if (Type == ReplicationBatchItem.ReplicationItemType.AttachmentTombstone ||
                         Type == ReplicationBatchItem.ReplicationItemType.CounterTombstone ||
                         Type == ReplicationBatchItem.ReplicationItemType.RevisionTombstone)
                {
                    KeyDispose.Dispose();
                }
            }
        }

        internal struct ReplicationAttachmentStream : IDisposable
        {
            public Slice Base64Hash;
            public ByteStringContext.InternalScope Base64HashDispose;

            public Stream Stream;

            public void Dispose()
            {
                Base64HashDispose.Dispose();
                Stream.Dispose();
            }
        }

        private unsafe ArraySegment<ReplicationItem> ReadItemsFromSource(int replicatedDocs, DocumentsOperationContext context,
            IncomingReplicationAllocator incomingReplicationAllocator, IncomingReplicationStatsScope stats)
        {
            var items = ArrayPool<ReplicationItem>.Shared.Rent(replicatedDocs);

            var documentRead = stats.For(ReplicationOperation.Incoming.DocumentRead, start: false);
            var attachmentRead = stats.For(ReplicationOperation.Incoming.AttachmentRead, start: false);
            var tombstoneRead = stats.For(ReplicationOperation.Incoming.TombstoneRead, start: false);

            for (var i = 0; i < replicatedDocs; i++)
            {
                stats.RecordInputAttempt();

                ref ReplicationItem item = ref items[i];
                item.Type = *(ReplicationBatchItem.ReplicationItemType*)ReadExactly(sizeof(byte));

                var changeVectorSize = *(int*)ReadExactly(sizeof(int));

                if (changeVectorSize != 0)
                    item.ChangeVector = Encoding.UTF8.GetString(ReadExactly(changeVectorSize), changeVectorSize);

                item.TransactionMarker = *(short*)ReadExactly(sizeof(short));

                if (item.Type == ReplicationBatchItem.ReplicationItemType.Attachment)
                {
                    stats.RecordAttachmentRead();

                    using (attachmentRead.Start())
                    {
                        var loweredKeySize = *(int*)ReadExactly(sizeof(int));
                        item.KeyDispose = Slice.From(context.Allocator, ReadExactly(loweredKeySize), loweredKeySize, out item.Key);

                        var nameSize = *(int*)ReadExactly(sizeof(int));
                        var name = Encoding.UTF8.GetString(ReadExactly(nameSize), nameSize);
                        item.NameDispose = DocumentIdWorker.GetStringPreserveCase(context, name, out item.Name);

                        var contentTypeSize = *(int*)ReadExactly(sizeof(int));
                        var contentType = Encoding.UTF8.GetString(ReadExactly(contentTypeSize), contentTypeSize);
                        item.ContentTypeDispose = DocumentIdWorker.GetStringPreserveCase(context, contentType, out item.ContentType);

                        var base64HashSize = *ReadExactly(sizeof(byte));
                        item.Base64HashDispose = Slice.From(context.Allocator, ReadExactly(base64HashSize), base64HashSize, out item.Base64Hash);
                    }
                }
                else if (item.Type == ReplicationBatchItem.ReplicationItemType.AttachmentTombstone)
                {
                    stats.RecordAttachmentTombstoneRead();

                    using (tombstoneRead.Start())
                    {
                        item.LastModifiedTicks = *(long*)ReadExactly(sizeof(long));

                        var keySize = *(int*)ReadExactly(sizeof(int));
                        item.KeyDispose = Slice.From(context.Allocator, ReadExactly(keySize), keySize, out item.Key);
                    }
                }
                else if (item.Type == ReplicationBatchItem.ReplicationItemType.RevisionTombstone)
                {
                    stats.RecordRevisionTombstoneRead();

                    using (tombstoneRead.Start())
                    {
                        item.LastModifiedTicks = *(long*)ReadExactly(sizeof(long));

                        var keySize = *(int*)ReadExactly(sizeof(int));
                        item.KeyDispose = Slice.From(context.Allocator, ReadExactly(keySize), keySize, out item.Key);

                        var collectionSize = *(int*)ReadExactly(sizeof(int));
                        Debug.Assert(collectionSize > 0);
                        item.Collection = Encoding.UTF8.GetString(ReadExactly(collectionSize), collectionSize);
                    }
                }
                else if (item.Type == ReplicationBatchItem.ReplicationItemType.Counter)
                {
                    var keySize = *(int*)ReadExactly(sizeof(int));
                    item.Id = Encoding.UTF8.GetString(ReadExactly(keySize), keySize);

                    var collectionSize = *(int*)ReadExactly(sizeof(int));
                    Debug.Assert(collectionSize > 0);
                    item.Collection = Encoding.UTF8.GetString(ReadExactly(collectionSize), collectionSize);

                    var nameSize = *(int*)ReadExactly(sizeof(int));
                    item.CounterName = Encoding.UTF8.GetString(ReadExactly(nameSize), nameSize);

                    item.CounterValue = *(long*)ReadExactly(sizeof(long));
                }
                else if (item.Type == ReplicationBatchItem.ReplicationItemType.CounterTombstone)
                {
                    var keySize = *(int*)ReadExactly(sizeof(int));
                    item.KeyDispose = Slice.From(context.Allocator, ReadExactly(keySize), keySize, out item.Key);

                    var collectionSize = *(int*)ReadExactly(sizeof(int));
                    Debug.Assert(collectionSize > 0);
                    item.Collection = Encoding.UTF8.GetString(ReadExactly(collectionSize), collectionSize);

                    item.LastModifiedTicks = *(long*)ReadExactly(sizeof(long));
                }
                else
                {
                    IncomingReplicationStatsScope scope;

                    if (item.Type != ReplicationBatchItem.ReplicationItemType.DocumentTombstone)
                    {
                        scope = documentRead;
                        stats.RecordDocumentRead();
                    }
                    else
                    {
                        scope = tombstoneRead;
                        stats.RecordDocumentTombstoneRead();
                    }

                    using (scope.Start())
                    {
                        item.LastModifiedTicks = *(long*)ReadExactly(sizeof(long));

                        item.Flags = *(DocumentFlags*)ReadExactly(sizeof(DocumentFlags)) | DocumentFlags.FromReplication;

                        var keySize = *(int*)ReadExactly(sizeof(int));
                        item.Id = Encoding.UTF8.GetString(ReadExactly(keySize), keySize);

                        var documentSize = item.DocumentSize = *(int*)ReadExactly(sizeof(int));
                        if (documentSize != -1) //if -1, then this is a tombstone
                        {
                            var mem = incomingReplicationAllocator.AllocateMemoryForDocument(documentSize);
                            ReadExactly(mem, documentSize);

                            item.Document = new BlittableJsonReaderObject(mem, documentSize, context);
                            item.Document.BlittableValidation();
                        }
                        else
                        {
                            // read the collection
                            var collectionSize = *(int*)ReadExactly(sizeof(int));
                            if (collectionSize != -1)
                            {
                                item.Collection = Encoding.UTF8.GetString(ReadExactly(collectionSize), collectionSize);
                            }
                        }
                    }
                }
            }

            return new ArraySegment<ReplicationItem>(items, 0, replicatedDocs);
        }

        private unsafe class IncomingReplicationAllocator : IDisposable
        {
            private readonly DocumentsOperationContext _context;
            private readonly long _maxSizeForContextUseInBytes;
            private readonly long _minSizeToAllocateNonContextUseInBytes;
            public long TotalDocumentsSizeInBytes { get; private set; }

            private List<Allocation> _nativeAllocationList;
            private Allocation _currentAllocation;

            public IncomingReplicationAllocator(DocumentsOperationContext context, DocumentDatabase database)
            {
                _context = context;

                var maxSizeForContextUse = database.Configuration.Replication.MaxSizeToSend * 2 ??
                              new Size(128, SizeUnit.Megabytes);

                _maxSizeForContextUseInBytes = maxSizeForContextUse.GetValue(SizeUnit.Bytes);
                var minSizeToNonContextAllocationInMb = PlatformDetails.Is32Bits ? 4 : 16;
                _minSizeToAllocateNonContextUseInBytes = new Size(minSizeToNonContextAllocationInMb, SizeUnit.Megabytes).GetValue(SizeUnit.Bytes);
            }

            public byte* AllocateMemoryForDocument(int size)
            {
                TotalDocumentsSizeInBytes += size;
                if (TotalDocumentsSizeInBytes <= _maxSizeForContextUseInBytes)
                {
                    _context.Allocator.Allocate(size, out var output);
                    return output.Ptr;
                }

                if (_currentAllocation == null || _currentAllocation.Free < size)
                {
                    // first allocation or we don't have enough space on the currently allocated chunk

                    // there can be a document that is larger than the minimum
                    var sizeToAllocate = Math.Max(size, _minSizeToAllocateNonContextUseInBytes);

                    var allocation = new Allocation(sizeToAllocate);
                    if (_nativeAllocationList == null)
                        _nativeAllocationList = new List<Allocation>();

                    _nativeAllocationList.Add(allocation);
                    _currentAllocation = allocation;
                }

                return _currentAllocation.GetMemory(size);
            }

            public void Dispose()
            {
                if (_nativeAllocationList == null)
                    return;

                foreach (var allocation in _nativeAllocationList)
                {
                    allocation.Dispose();
                }
            }

            private class Allocation : IDisposable
            {
                private readonly byte* _ptr;
                private readonly long _allocationSize;
                private readonly NativeMemory.ThreadStats _threadStats;
                private long _used;
                public long Free => _allocationSize - _used;

                public Allocation(long allocationSize)
                {
                    _ptr = NativeMemory.AllocateMemory(allocationSize, out var threadStats);
                    _allocationSize = allocationSize;
                    _threadStats = threadStats;
                }

                public byte* GetMemory(long size)
                {
                    ThrowOnPointerOutOfRange(size);

                    var mem = _ptr + _used;
                    _used += size;
                    return mem;
                }

                [Conditional("DEBUG")]
                private void ThrowOnPointerOutOfRange(long size)
                {
                    if (_used + size > _allocationSize)
                        throw new InvalidOperationException(
                            $"Not enough space to allocate the requested size: {new Size(size, SizeUnit.Bytes)}, " +
                            $"used: {new Size(_used, SizeUnit.Bytes)}, " +
                            $"total allocation size: {new Size(_allocationSize, SizeUnit.Bytes)}");
                }

                public void Dispose()
                {
                    NativeMemory.Free(_ptr, _allocationSize, _threadStats);
                }
            }
        }

        private unsafe void ReadAttachmentStreamsFromSource(int attachmentStreamCount, 
            DocumentsOperationContext context, DataForReplicationCommand dataForReplicationCommand)
        {
            if (attachmentStreamCount == 0)
                return;

            var items = ArrayPool<ReplicationAttachmentStream>.Shared.Rent(attachmentStreamCount);
            var replicatedAttachmentStreams = new Dictionary<Slice, ReplicationAttachmentStream>(SliceComparer.Instance);

            for (var i = 0; i < attachmentStreamCount; i++)
            {
                var type = *(ReplicationBatchItem.ReplicationItemType*)ReadExactly(sizeof(byte));
                Debug.Assert(type == ReplicationBatchItem.ReplicationItemType.AttachmentStream);

                ref ReplicationAttachmentStream attachment = ref items[i];

                var base64HashSize = *ReadExactly(sizeof(byte));
                attachment.Base64HashDispose = Slice.From(context.Allocator, ReadExactly(base64HashSize), base64HashSize, out attachment.Base64Hash);

                var streamLength = *(long*)ReadExactly(sizeof(long));
                attachment.Stream = _attachmentStreamsTempFile.StartNewStream();
                ReadExactly(streamLength, attachment.Stream);
                attachment.Stream.Flush();
                replicatedAttachmentStreams[attachment.Base64Hash] = attachment;
            }

            dataForReplicationCommand.AttachmentStreams = new ArraySegment<ReplicationAttachmentStream>(items, 0, attachmentStreamCount);
            dataForReplicationCommand.ReplicatedAttachmentStreams = replicatedAttachmentStreams;
        }

        private void AddReplicationPulse(ReplicationPulseDirection direction, string exceptionMessage = null)
        {
            HandleReplicationPulse?.Invoke(new LiveReplicationPulsesCollector.ReplicationPulse
            {
                OccurredAt = SystemTime.UtcNow,
                Direction = direction,
                From = ConnectionInfo,
                ExceptionMessage = exceptionMessage
            });
        }

        private void AddReplicationPerformance(IncomingReplicationStatsAggregator stats)
        {
            _lastReplicationStats.Enqueue(stats);

            while (_lastReplicationStats.Count > 25)
                _lastReplicationStats.TryDequeue(out stats);
        }

        public void Dispose()
        {
            var releaser = _copiedBuffer.ReleaseBuffer;
            try
            {
                if (_log.IsInfoEnabled)
                    _log.Info($"Disposing IncomingReplicationHandler ({FromToString})");
                _cts.Cancel();
                try
                {
                    _connectionOptionsDisposable?.Dispose();
                }
                catch (Exception)
                {
                }
                try
                {
                    _stream.Dispose();
                }
                catch (Exception)
                {
                }
                try
                {
                    _tcpClient.Dispose();
                }
                catch (Exception)
                {
                }

                try
                {
                    _connectionOptions.Dispose();
                }
                catch
                {
                    // do nothing
                }

                _replicationFromAnotherSource.Set();

                if (_incomingWork != PoolOfThreads.LongRunningWork.Current)
                {
                    try
                    {
                        _incomingWork?.Join(int.MaxValue);
                    }
                    catch (ThreadStateException)
                    {
                        // expected if the thread hasn't been started yet
                    }
                }

                _incomingWork = null;
                _cts.Dispose();

                _attachmentStreamsTempFile.Dispose();

            }
            finally
            {
                try
                {
                    releaser?.Dispose();
                }
                catch (Exception)
                {
                    // can't do anything about it...
                }
            }

        }

        protected void OnFailed(Exception exception, IncomingReplicationHandler instance) => Failed?.Invoke(instance, exception);
        protected void OnDocumentsReceived(IncomingReplicationHandler instance) => DocumentsReceived?.Invoke(instance);

        internal class MergedUpdateDatabaseChangeVectorCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly string _changeVector;
            private readonly long _lastDocumentEtag;
            private readonly string _sourceDatabaseId;
            private readonly AsyncManualResetEvent _trigger;

            public MergedUpdateDatabaseChangeVectorCommand(string changeVector, long lastDocumentEtag, string sourceDatabaseId, AsyncManualResetEvent trigger)
            {
                _changeVector = changeVector;
                _lastDocumentEtag = lastDocumentEtag;
                _sourceDatabaseId = sourceDatabaseId;
                _trigger = trigger;
            }

            protected override int ExecuteCmd(DocumentsOperationContext context)
            {
                var operationsCount = 0;
                var lastReplicatedEtag = DocumentsStorage.GetLastReplicatedEtagFrom(context, _sourceDatabaseId);
                if (_lastDocumentEtag > lastReplicatedEtag)
                {
                    DocumentsStorage.SetLastReplicatedEtagFrom(context, _sourceDatabaseId, _lastDocumentEtag);
                    operationsCount++;
                }

                var current = context.LastDatabaseChangeVector ?? DocumentsStorage.GetDatabaseChangeVector(context);
                var conflictStatus = ChangeVectorUtils.GetConflictStatus(_changeVector, current);
                if (conflictStatus != ConflictStatus.Update)
                    return operationsCount;

                operationsCount++;
                context.LastDatabaseChangeVector = ChangeVectorUtils.MergeVectors(current, _changeVector);
                context.Transaction.InnerTransaction.LowLevelTransaction.OnDispose += _ =>
                {
                    try
                    {
                        _trigger.Set();
                    }
                    catch
                    {
                        //
                    }
                };

                return operationsCount;
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto(JsonOperationContext context)
            {
                return new MergedUpdateDatabaseChangeVectorCommandDto
                {
                    ChangeVector = _changeVector,
                    LastDocumentEtag = _lastDocumentEtag,
                    SourceDatabaseId = _sourceDatabaseId
                };
            }
        }

        internal unsafe class MergedDocumentReplicationCommand : TransactionOperationsMerger.MergedTransactionCommand
        {
            private readonly long _lastEtag;
            private readonly DataForReplicationCommand _replicationInfo;

            public MergedDocumentReplicationCommand(DataForReplicationCommand replicationInfo, long lastEtag)
            {
                _replicationInfo = replicationInfo;
                _lastEtag = lastEtag;
            }

            protected override int ExecuteCmd(DocumentsOperationContext context)
            {
                try
                {
                    IsIncomingReplication = true;

                    var operationsCount = 0;

                    var database = _replicationInfo.DocumentDatabase;

                    context.LastDatabaseChangeVector = context.LastDatabaseChangeVector ?? DocumentsStorage.GetDatabaseChangeVector(context);
                    foreach (var item in _replicationInfo.ReplicatedItems)
                    {
                        context.TransactionMarkerOffset = item.TransactionMarker;
                        ++operationsCount;
                        using (item)
                        {
                            Debug.Assert(item.Flags.Contain(DocumentFlags.Artificial) == false);

                            var rcvdChangeVector = item.ChangeVector;

                            context.LastDatabaseChangeVector = ChangeVectorUtils.MergeVectors(item.ChangeVector, context.LastDatabaseChangeVector);
                            if (item.Type == ReplicationBatchItem.ReplicationItemType.Attachment)
                            {
                                database.DocumentsStorage.AttachmentsStorage.PutDirect(context, item.Key, item.Name,
                                    item.ContentType, item.Base64Hash, item.ChangeVector);

                                if (_replicationInfo.ReplicatedAttachmentStreams.TryGetValue(item.Base64Hash, out ReplicationAttachmentStream attachmentStream))
                                {
                                    database.DocumentsStorage.AttachmentsStorage.PutAttachmentStream(context, item.Key, attachmentStream.Base64Hash,
                                        attachmentStream.Stream);
                                    _replicationInfo.ReplicatedAttachmentStreams.Remove(item.Base64Hash);
                                }
                            }
                            else if (item.Type == ReplicationBatchItem.ReplicationItemType.AttachmentTombstone)
                            {
                                database.DocumentsStorage.AttachmentsStorage.DeleteAttachmentDirect(context, item.Key, false, "$fromReplication", null, rcvdChangeVector,
                                    item.LastModifiedTicks);
                            }
                            else if (item.Type == ReplicationBatchItem.ReplicationItemType.RevisionTombstone)
                            {
                                database.DocumentsStorage.RevisionsStorage.DeleteRevision(context, item.Key, item.Collection, rcvdChangeVector, item.LastModifiedTicks);
                            }
                            else if (item.Type == ReplicationBatchItem.ReplicationItemType.Counter)
                            {
                                database.DocumentsStorage.CountersStorage.PutCounter(context,
                                    item.Id, item.Collection, item.CounterName, item.ChangeVector,
                                    item.CounterValue);
                            }
                            else if (item.Type == ReplicationBatchItem.ReplicationItemType.CounterTombstone)
                            {
                                database.DocumentsStorage.CountersStorage.DeleteCounter(context, item.Key, item.Collection,
                                    item.ChangeVector,
                                    item.LastModifiedTicks,
                                    // we force the tombstone because we have to replicate it further
                                    forceTombstone: true);
                            }
                            else
                            {
                                BlittableJsonReaderObject document = null;

                                // no need to load document data for tombstones
                                // document size == -1 --> doc is a tombstone
                                if (item.DocumentSize >= 0)
                                {
                                    // if something throws at this point, this means something is really wrong and we should stop receiving documents.
                                    // the other side will receive negative ack and will retry sending again.
                                    document = item.Document;

                                    try
                                    {
                                        AssertAttachmentsFromReplication(context, item.Id, document);
                                    }
                                    catch (MissingAttachmentException)
                                    {
                                        if (_replicationInfo.SupportedFeatures.Replication.MissingAttachments)
                                        {
                                            throw;
                                        }

                                        database.NotificationCenter.Add(AlertRaised.Create(
                                            database.Name,
                                            IncomingReplicationStr,
                                            $"Detected missing attachments for document {item.Id} with the following hashes:" +
                                            $" ({string.Join(',', GetAttachmentsHashesFromDocumentMetadata(document))}).",
                                            AlertType.ReplicationMissingAttachments,
                                            NotificationSeverity.Warning));
                                    }
                                }

                                if (item.Flags.Contain(DocumentFlags.Revision))
                                {
                                    database.DocumentsStorage.RevisionsStorage.Put(
                                        context,
                                        item.Id,
                                        document,
                                        item.Flags,
                                        NonPersistentDocumentFlags.FromReplication,
                                        rcvdChangeVector,
                                        item.LastModifiedTicks);
                                    continue;
                                }

                                if (item.Flags.Contain(DocumentFlags.DeleteRevision))
                                {
                                    database.DocumentsStorage.RevisionsStorage.Delete(
                                        context,
                                        item.Id,
                                        document,
                                        item.Flags,
                                        NonPersistentDocumentFlags.FromReplication,
                                        rcvdChangeVector,
                                        item.LastModifiedTicks);
                                    continue;
                                }

                                var hasRemoteClusterTx = item.Flags.Contain(DocumentFlags.FromClusterTransaction);
                                var conflictStatus = ConflictsStorage.GetConflictStatusForDocument(context, item.Id, item.ChangeVector, out var conflictingVector,
                                    out var hasLocalClusterTx);

                                var flags = item.Flags;
                                var resolvedDocument = document;
                                switch (conflictStatus)
                                {
                                    case ConflictStatus.Update:

                                        if (resolvedDocument != null)
                                        {
                                            AttachmentsStorage.AssertAttachments(document, item.Flags);

                                            database.DocumentsStorage.Put(context, item.Id, null, resolvedDocument, item.LastModifiedTicks,
                                                rcvdChangeVector, flags, NonPersistentDocumentFlags.FromReplication);
                                        }
                                        else
                                        {
                                            using (DocumentIdWorker.GetSliceFromId(context, item.Id, out Slice keySlice))
                                            {
                                                database.DocumentsStorage.Delete(
                                                    context, keySlice, item.Id, null,
                                                    item.LastModifiedTicks,
                                                    rcvdChangeVector,
                                                    new CollectionName(item.Collection),
                                                    NonPersistentDocumentFlags.FromReplication,
                                                    flags);
                                            }
                                        }

                                        break;
                                    case ConflictStatus.Conflict:
                                        if (_replicationInfo.Logger.IsInfoEnabled)
                                            _replicationInfo.Logger.Info(
                                                $"Conflict check resolved to Conflict operation, resolving conflict for doc = {item.Id}, with change vector = {item.ChangeVector}");

                                        // we will always prefer the local
                                        if (hasLocalClusterTx)
                                        {
                                            // we have to strip the cluster tx flag from the local document
                                            var local = database.DocumentsStorage.GetDocumentOrTombstone(context, item.Id, throwOnConflict: false);
                                            flags = item.Flags.Strip(DocumentFlags.FromClusterTransaction);
                                            if (local.Document != null)
                                            {
                                                rcvdChangeVector = ChangeVectorUtils.MergeVectors(rcvdChangeVector, local.Document.ChangeVector);
                                                resolvedDocument = local.Document.Data.Clone(context);
                                            }
                                            else if (local.Tombstone != null)
                                            {
                                                rcvdChangeVector = ChangeVectorUtils.MergeVectors(rcvdChangeVector, local.Tombstone.ChangeVector);
                                                resolvedDocument = null;
                                            }
                                            else
                                            {
                                                throw new InvalidOperationException("Local cluster tx but no matching document / tombstone for: " + item.Id +
                                                                                    ", this should not be possible");
                                            }

                                            goto case ConflictStatus.Update;
                                        }

                                        // otherwise we will choose the remote document from the transaction
                                        if (hasRemoteClusterTx)
                                        {
                                            flags = flags.Strip(DocumentFlags.FromClusterTransaction);
                                            goto case ConflictStatus.Update;
                                        }
                                        else
                                        {
                                            // if the conflict is going to be resolved locally, that means that we have local work to do
                                            // that we need to distribute to our siblings
                                            IsIncomingReplication = false;
                                            _replicationInfo.ConflictManager.HandleConflictForDocument(context, item.Id, item.Collection, item.LastModifiedTicks,
                                                document,
                                                rcvdChangeVector, conflictingVector, item.Flags);
                                        }

                                        break;
                                    case ConflictStatus.AlreadyMerged:
                                        // we have to do nothing here
                                        break;
                                    default:
                                        throw new ArgumentOutOfRangeException(nameof(conflictStatus),
                                            "Invalid ConflictStatus: " + conflictStatus);
                                }
                            }
                        }
                    }

                    Debug.Assert(_replicationInfo.ReplicatedAttachmentStreams == null ||
                                 _replicationInfo.ReplicatedAttachmentStreams.Count == 0,
                        "We should handle all attachment streams during WriteAttachment.");
                    Debug.Assert(context.LastDatabaseChangeVector != null);

                    // instead of : SetLastReplicatedEtagFrom -> _incoming.ConnectionInfo.SourceDatabaseId, _lastEtag , we will store in context and write once right before commit (one time instead of repeating on all docs in the same Tx)
                    if (context.LastReplicationEtagFrom == null)
                        context.LastReplicationEtagFrom = new Dictionary<string, long>();
                    context.LastReplicationEtagFrom[_replicationInfo.SourceDatabaseId] = _lastEtag;
                    return operationsCount;
                }
                finally
                {
                    IsIncomingReplication = false;
                }
            }

            public readonly string IncomingReplicationStr = "Incoming Replication";

            public void AssertAttachmentsFromReplication(DocumentsOperationContext context, string id, BlittableJsonReaderObject document)
            {
                foreach (LazyStringValue hash in GetAttachmentsHashesFromDocumentMetadata(document))
                {
                    if (_replicationInfo.DocumentDatabase.DocumentsStorage.AttachmentsStorage.AttachmentExists(context, hash) == false)
                    {
                        var msg = $"Document '{id}' has attachment '{hash?.ToString() ?? "unknown"}' " +
                                  $"listed as one of his attachments but it doesn't exist in the attachment storage";

                        throw new MissingAttachmentException(msg);
                    }
                }
            }

            public IEnumerable<LazyStringValue> GetAttachmentsHashesFromDocumentMetadata(BlittableJsonReaderObject document)
            {
                if (document.TryGet(Raven.Client.Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) &&
                    metadata.TryGet(Raven.Client.Constants.Documents.Metadata.Attachments, out BlittableJsonReaderArray attachments))
                {
                    foreach (BlittableJsonReaderObject attachment in attachments)
                    {
                        if (attachment.TryGet(nameof(AttachmentName.Hash), out LazyStringValue hash))
                        {
                            yield return hash;
                        }
                    }
                }
            }

            public override TransactionOperationsMerger.IReplayableCommandDto<TransactionOperationsMerger.MergedTransactionCommand> ToDto(JsonOperationContext context)
            {
                var replicatedAttachmentStreams = _replicationInfo.ReplicatedAttachmentStreams?
                    .Select(kv => KeyValuePair.Create(kv.Key.ToString(), kv.Value.Stream))
                    .ToArray();

                return new MergedDocumentReplicationCommandDto
                {
                    LastEtag = _lastEtag,
                    SupportedFeatures = _replicationInfo.SupportedFeatures,
                    ReplicatedItemDtos = _replicationInfo.ReplicatedItems.Select(i => ReplicationItemToDto(context, i)).ToArray(),
                    SourceDatabaseId = _replicationInfo.SourceDatabaseId,
                    ReplicatedAttachmentStreams = replicatedAttachmentStreams
                };
            }

            private static ReplicationItemDto ReplicationItemToDto(JsonOperationContext context, ReplicationItem item)
            {
                var dto = new ReplicationItemDto
                {
                    TransactionMarker = item.TransactionMarker,
                    Type = item.Type,
                    Id = item.Id,
                    ChangeVector = item.ChangeVector,
                    Document = item.Document?.Clone(context),
                    DocumentSize = item.DocumentSize,
                    Collection = item.Collection,
                    LastModifiedTicks = item.LastModifiedTicks,
                    Flags = item.Flags,
                    Key = item.Key.ToString(),
                    Base64Hash = item.Base64Hash.ToString()
                };

                dto.Name = item.Name.Content.HasValue
                    ? context.GetLazyStringValue(item.Name.Content.Ptr)
                    : null;

                dto.ContentType = item.Name.Content.HasValue
                    ? context.GetLazyStringValue(item.ContentType.Content.Ptr)
                    : null;

                return dto;
            }
        }
    }

    internal class MergedUpdateDatabaseChangeVectorCommandDto : TransactionOperationsMerger.IReplayableCommandDto<IncomingReplicationHandler.MergedUpdateDatabaseChangeVectorCommand>
    {
        public string ChangeVector;
        public long LastDocumentEtag;
        public string SourceDatabaseId;

        public IncomingReplicationHandler.MergedUpdateDatabaseChangeVectorCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
        {
            var command = new IncomingReplicationHandler.MergedUpdateDatabaseChangeVectorCommand(ChangeVector, LastDocumentEtag, SourceDatabaseId, new AsyncManualResetEvent());
            return command;
        }
    }

    internal class MergedDocumentReplicationCommandDto : TransactionOperationsMerger.IReplayableCommandDto<IncomingReplicationHandler.MergedDocumentReplicationCommand>
    {
        public ReplicationItemDto[] ReplicatedItemDtos;
        public long LastEtag;
        public TcpConnectionHeaderMessage.SupportedFeatures SupportedFeatures;
        public string SourceDatabaseId;
        public KeyValuePair<string, Stream>[] ReplicatedAttachmentStreams;

        public IncomingReplicationHandler.MergedDocumentReplicationCommand ToCommand(DocumentsOperationContext context, DocumentDatabase database)
        {
            var replicatedItemsCount = ReplicatedItemDtos.Length;
            var replicationItems = ArrayPool<IncomingReplicationHandler.ReplicationItem>.Shared.Rent(replicatedItemsCount);
            for (var i = 0; i < replicatedItemsCount; i++)
            {
                replicationItems[i] = ReplicatedItemDtos[i].ToItem(context);
            }

            ArraySegment<IncomingReplicationHandler.ReplicationAttachmentStream> attachmentStreams = null;
            Dictionary<Slice, IncomingReplicationHandler.ReplicationAttachmentStream> replicatedAttachmentStreams = null;
            if (ReplicatedAttachmentStreams != null)
            {
                var attachmentStreamsCount = ReplicatedAttachmentStreams.Length;
                var replicationAttachmentStreams = ArrayPool<IncomingReplicationHandler.ReplicationAttachmentStream>.Shared.Rent(attachmentStreamsCount);
                for (var i = 0; i < attachmentStreamsCount; i++)
                {
                    var replicationAttachmentStream = ReplicatedAttachmentStreams[i];
                    replicationAttachmentStreams[i] = CreateReplicationAttachmentStream(context, replicationAttachmentStream);
                }

                attachmentStreams = new ArraySegment<IncomingReplicationHandler.ReplicationAttachmentStream>(replicationAttachmentStreams, 0, attachmentStreamsCount);
                replicatedAttachmentStreams = attachmentStreams.ToDictionary(i => i.Base64Hash, SliceComparer.Instance);
            }

            var dataForReplicationCommand = new IncomingReplicationHandler.DataForReplicationCommand
            {
                DocumentDatabase = database,
                ConflictManager = new ConflictManager(database, database.ReplicationLoader.ConflictResolver),
                SourceDatabaseId = SourceDatabaseId,
                ReplicatedItems = new ArraySegment<IncomingReplicationHandler.ReplicationItem>(replicationItems, 0, replicatedItemsCount),
                AttachmentStreams = attachmentStreams,
                ReplicatedAttachmentStreams = replicatedAttachmentStreams,
                SupportedFeatures = SupportedFeatures,
                Logger = LoggingSource.Instance.GetLogger<IncomingReplicationHandler>(database.Name)
            };

            return new IncomingReplicationHandler.MergedDocumentReplicationCommand(dataForReplicationCommand, LastEtag);
        }

        private IncomingReplicationHandler.ReplicationAttachmentStream CreateReplicationAttachmentStream(DocumentsOperationContext context, KeyValuePair<string, Stream> arg)
        {
            var attachmentStream = new IncomingReplicationHandler.ReplicationAttachmentStream();
            attachmentStream.Stream = arg.Value;
            attachmentStream.Base64HashDispose = Slice.From(context.Allocator, arg.Key, ByteStringType.Immutable, out attachmentStream.Base64Hash);
            return attachmentStream;
        }
    }

    internal class ReplicationItemDto
    {
        public short TransactionMarker;
        public ReplicationBatchItem.ReplicationItemType Type;

        #region Document

        public string Id;
        public int Position;
        public string ChangeVector;
        public BlittableJsonReaderObject Document;
        public int DocumentSize;
        public string Collection;
        public long LastModifiedTicks;
        public DocumentFlags Flags;

        #endregion

        #region Attachment

        public string Key;
        public string Name;
        public string ContentType;
        public string Base64Hash;

        #endregion

        public IncomingReplicationHandler.ReplicationItem ToItem(DocumentsOperationContext context)
        {
            var item = new IncomingReplicationHandler.ReplicationItem
            {
                TransactionMarker = TransactionMarker,
                Type = Type,
                Id = Id,
                Document = Document,
                ChangeVector = ChangeVector,
                DocumentSize = DocumentSize,
                Collection = Collection,
                LastModifiedTicks = LastModifiedTicks,
                Flags = Flags
            };

            if (Name != null)
            {
                item.NameDispose = DocumentIdWorker.GetStringPreserveCase(context, Name, out item.Name);
            }

            if (ContentType != null)
            {
                item.ContentTypeDispose = DocumentIdWorker.GetStringPreserveCase(context, ContentType, out item.ContentType);
            }

            item.KeyDispose = Slice.From(context.Allocator, Key, ByteStringType.Immutable, out item.Key);
            item.Base64HashDispose = Slice.From(context.Allocator, Base64Hash, ByteStringType.Immutable, out item.Base64Hash);

            return item;
        }
    }
}
