using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using Raven.Client;
using Raven.Client.Documents.Operations.Revisions;
using Raven.Client.ServerWide;
using Raven.Server.Json;
using Raven.Server.NotificationCenter.Notifications;
using Raven.Server.ServerWide.Context;
using Raven.Server.Utils;
using Sparrow;
using Sparrow.Binary;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using Sparrow.Logging;
using Sparrow.Utils;
using Voron;
using Voron.Data.Tables;
using Voron.Impl;
using static Raven.Server.Documents.DocumentsStorage;

namespace Raven.Server.Documents.Revisions
{
    public unsafe class RevisionsStorage
    {
        private static readonly Slice IdAndEtagSlice;
        public static readonly Slice DeleteRevisionEtagSlice;
        public static readonly Slice AllRevisionsEtagsSlice;
        public static readonly Slice CollectionRevisionsEtagsSlice;
        private static readonly Slice RevisionsCountSlice;
        private static readonly Slice RevisionsTombstonesSlice;
        private static readonly Slice RevisionsPrefix;
        public static Slice ResolvedFlagByEtagSlice;

        public static readonly string RevisionsTombstones = "Revisions.Tombstones";

        public static readonly TableSchema RevisionsSchema = new TableSchema()
        {
            TableType = (byte)TableType.Revisions
        };

        public static RevisionsConfiguration ConflictConfiguration;

        private readonly DocumentDatabase _database;
        private readonly DocumentsStorage _documentsStorage;
        public RevisionsConfiguration Configuration { get; private set; }
        public readonly RevisionsOperations Operations;
        private HashSet<string> _tableCreated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Logger _logger;

        public enum RevisionsTable
        {
            /* ChangeVector is the table's key as it's unique and will avoid conflicts (by replication) */
            ChangeVector = 0,
            LowerId = 1,
            /* We are you using the record separator in order to avoid loading another documents that has the same ID prefix, 
                e.g. fitz(record-separator)01234567 and fitz0(record-separator)01234567, without the record separator we would have to load also fitz0 and filter it. */
            RecordSeparator = 2,
            Etag = 3, // etag to keep the insertion order
            Id = 4,
            Document = 5,
            Flags = 6,
            DeletedEtag = 7,
            LastModified = 8,
            TransactionMarker = 9,

            // Field for finding the resolved conflicts
            Resolved = 10,
            SwappedLastModified = 11,
        }

        public const long NotDeletedRevisionMarker = 0;

        private readonly RevisionsCollectionConfiguration _emptyConfiguration = new RevisionsCollectionConfiguration { Disabled = true };

        public RevisionsStorage(DocumentDatabase database, Transaction tx)
        {
            _database = database;
            _documentsStorage = _database.DocumentsStorage;
            _logger = LoggingSource.Instance.GetLogger<RevisionsStorage>(database.Name);
            Operations = new RevisionsOperations(_database);
            ConflictConfiguration = new RevisionsConfiguration
            {
                Default = new RevisionsCollectionConfiguration
                {
                    MinimumRevisionAgeToKeep = TimeSpan.FromDays(45),
                    Disabled = false
                }
            };
            CreateTrees(tx);
        }

        public Table EnsureRevisionTableCreated(Transaction tx, CollectionName collection)
        {
            var tableName = collection.GetTableName(CollectionTableType.Revisions);

            if (_tableCreated.Contains(collection.Name) == false)
            {
                // RavenDB-11705: It is possible that this will revert if the transaction
                // aborts, so we must record this only after the transaction has been committed
                // note that calling the Create() method multiple times is a noop
                RevisionsSchema.Create(tx, tableName, 16);
                tx.LowLevelTransaction.OnDispose += _ =>
                 {
                     if (tx.LowLevelTransaction.Committed == false)
                         return;

                     // not sure if we can _rely_ on the tx write lock here, so let's be safe and create
                     // a new instance, just in case 
                     _tableCreated = new HashSet<string>(_tableCreated, StringComparer.OrdinalIgnoreCase)
                     {
                         collection.Name
                     };
                 };
            }
            return tx.OpenTable(RevisionsSchema, tableName);
        }

        static RevisionsStorage()
        {
            using (StorageEnvironment.GetStaticContext(out var ctx))
            {
                Slice.From(ctx, "RevisionsChangeVector", ByteStringType.Immutable, out var changeVectorSlice);
                Slice.From(ctx, "RevisionsIdAndEtag", ByteStringType.Immutable, out IdAndEtagSlice);
                Slice.From(ctx, "DeleteRevisionEtag", ByteStringType.Immutable, out DeleteRevisionEtagSlice);
                Slice.From(ctx, "AllRevisionsEtags", ByteStringType.Immutable, out AllRevisionsEtagsSlice);
                Slice.From(ctx, "CollectionRevisionsEtags", ByteStringType.Immutable, out CollectionRevisionsEtagsSlice);
                Slice.From(ctx, "RevisionsCount", ByteStringType.Immutable, out RevisionsCountSlice);
                Slice.From(ctx, nameof(ResolvedFlagByEtagSlice), ByteStringType.Immutable, out ResolvedFlagByEtagSlice);
                Slice.From(ctx, RevisionsTombstones, ByteStringType.Immutable, out RevisionsTombstonesSlice);
                Slice.From(ctx, CollectionName.GetTablePrefix(CollectionTableType.Revisions), ByteStringType.Immutable, out RevisionsPrefix);
                RevisionsSchema.DefineKey(new TableSchema.SchemaIndexDef
                {
                    StartIndex = (int)RevisionsTable.ChangeVector,
                    Count = 1,
                    Name = changeVectorSlice,
                    IsGlobal = true
                });
                RevisionsSchema.DefineIndex(new TableSchema.SchemaIndexDef
                {
                    StartIndex = (int)RevisionsTable.LowerId,
                    Count = 3,
                    Name = IdAndEtagSlice,
                    IsGlobal = true
                });
                RevisionsSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
                {
                    StartIndex = (int)RevisionsTable.Etag,
                    Name = AllRevisionsEtagsSlice,
                    IsGlobal = true
                });
                RevisionsSchema.DefineFixedSizeIndex(new TableSchema.FixedSizeSchemaIndexDef
                {
                    StartIndex = (int)RevisionsTable.Etag,
                    Name = CollectionRevisionsEtagsSlice
                });
                RevisionsSchema.DefineIndex(new TableSchema.SchemaIndexDef
                {
                    StartIndex = (int)RevisionsTable.DeletedEtag,
                    Count = 1,
                    Name = DeleteRevisionEtagSlice,
                    IsGlobal = true
                });
                RevisionsSchema.DefineIndex(new TableSchema.SchemaIndexDef
                {
                    StartIndex = (int)RevisionsTable.Resolved,
                    Count = 2,
                    Name = ResolvedFlagByEtagSlice,
                    IsGlobal = true
                });
            }
        }

        public void InitializeFromDatabaseRecord(DatabaseRecord dbRecord)
        {
            try
            {
                var revisions = dbRecord.Revisions;
                if (revisions == null ||
                    (revisions.Default == null && revisions.Collections.Count == 0))
                {
                    Configuration = null;
                    return;
                }

                if (revisions.Equals(Configuration))
                    return;

                Configuration = revisions;

                using (var tx = _database.DocumentsStorage.Environment.WriteTransaction())
                {
                    foreach (var collection in Configuration.Collections)
                    {
                        if (collection.Value.Disabled)
                            continue;
                        EnsureRevisionTableCreated(tx, new CollectionName(collection.Key));
                    }

                    CreateTrees(tx);

                    tx.Commit();
                }

                if (_logger.IsInfoEnabled)
                    _logger.Info("Revisions configuration changed");
            }
            catch (Exception e)
            {
                var msg = "Cannot enable revisions for documents as the revisions configuration " +
                          "in the database record is missing or not valid.";
                _database.NotificationCenter.Add(AlertRaised.Create(
                    _database.Name,
                    $"Revisions error in {_database.Name}", msg,
                    AlertType.RevisionsConfigurationNotValid, NotificationSeverity.Error, _database.Name));
                if (_logger.IsOperationsEnabled)
                    _logger.Operations(msg, e);
            }
        }

        private static void CreateTrees(Transaction tx)
        {
            tx.CreateTree(RevisionsCountSlice);
            TombstonesSchema.Create(tx, RevisionsTombstonesSlice, 16);
        }

        public void AssertFixedSizeTrees(Transaction tx)
        {
            tx.OpenTable(RevisionsSchema, RevisionsCountSlice).AssertValidFixedSizeTrees();
            tx.OpenTable(TombstonesSchema, RevisionsTombstonesSlice).AssertValidFixedSizeTrees();
        }

        public RevisionsCollectionConfiguration GetRevisionsConfiguration(string collection, DocumentFlags flags = DocumentFlags.None)
        {
            if (Configuration == null)
                return ConflictConfiguration.Default;

            if (Configuration.Collections != null &&
                Configuration.Collections.TryGetValue(collection, out RevisionsCollectionConfiguration configuration))
            {
                return configuration;
            }
            if (flags.Contain(DocumentFlags.Resolved) || flags.Contain(DocumentFlags.Conflicted))
            {
                return ConflictConfiguration.Default;
            }
            return Configuration.Default ?? _emptyConfiguration;
        }

        public bool ShouldVersionDocument(CollectionName collectionName, NonPersistentDocumentFlags nonPersistentFlags,
            BlittableJsonReaderObject existingDocument, BlittableJsonReaderObject document, 
            DocumentsOperationContext context, string id, 
            ref DocumentFlags documentFlags, out RevisionsCollectionConfiguration configuration)
        {
            configuration = GetRevisionsConfiguration(collectionName.Name);

            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromRevision))
                return false;

            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromSmuggler))
            {
                if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.ByCountersUpdate))
                    return false;

                if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.ByAttachmentUpdate))
                    return false;

                if (configuration == ConflictConfiguration.Default || configuration.Disabled)
                    return false;
            }

            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.Resolved))
                return true;

            if (Configuration == null)
                return false;

            if (configuration.Disabled)
                return false;

            if (configuration.MinimumRevisionsToKeep == 0)
            {
                DeleteRevisionsFor(context, id);
                documentFlags = documentFlags.Strip(DocumentFlags.HasRevisions);
                return false;
            }

            if (existingDocument == null)
            {
                if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.SkipRevisionCreation))
                {
                    // Smuggler is configured to avoid creating new revisions during import
                    return false;
                }

                // we are not going to create a revision if it's an import from v3
                // (since this import is going to import revisions as well)
                if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.LegacyHasRevisions))
                {
                    documentFlags |= DocumentFlags.HasRevisions;
                    return false;
                }

                return true;
            }

            // compare the contents of the existing and the new document
            if (DocumentCompare.IsEqualTo(existingDocument, document, false) != DocumentCompareResult.NotEqual)
            {
                // no need to create a new revision, both documents have identical content
                return false;
            }

            return true;
        }

        public void Put(DocumentsOperationContext context, string id, BlittableJsonReaderObject document,
            DocumentFlags flags, NonPersistentDocumentFlags nonPersistentFlags, string changeVector, long lastModifiedTicks,
            RevisionsCollectionConfiguration configuration = null, CollectionName collectionName = null)
        {
            Debug.Assert(changeVector != null, "Change vector must be set");
            Debug.Assert(lastModifiedTicks != DateTime.MinValue.Ticks, "last modified ticks must be set");

            BlittableJsonReaderObject.AssertNoModifications(document, id, assertChildren: true);

            if (collectionName == null)
                collectionName = _database.DocumentsStorage.ExtractCollectionName(context, document);
            if (configuration == null)
                configuration = GetRevisionsConfiguration(collectionName.Name);

            using (DocumentIdWorker.GetLowerIdSliceAndStorageKey(context, id, out Slice lowerId, out Slice idSlice))
            using (Slice.From(context.Allocator, changeVector, out Slice changeVectorSlice))
            {
                var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);
                var revisionExists = table.ReadByKey(changeVectorSlice, out var tvr);
                
                if (revisionExists)
                {
                    MarkRevisionsAsConflictedIfNeeded(context, lowerId, idSlice, flags, tvr, table, changeVectorSlice);
                    return;
                }

                // We want the revision's attachments to have a lower etag than the revision itself
                if (flags.Contain(DocumentFlags.HasAttachments) &&
                    flags.Contain(DocumentFlags.Revision) == false)
                {
                    _documentsStorage.AttachmentsStorage.RevisionAttachments(context, lowerId, changeVectorSlice);
                }

                document = RecreateCountersIfNeeded(context, id, document);

                PutFromRevisionIfChangeVectorIsGreater(context, document, id, changeVector, lastModifiedTicks, flags, nonPersistentFlags);

                flags |= DocumentFlags.Revision;
                var etag = _database.DocumentsStorage.GenerateNextEtag();
                var newEtagSwapBytes = Bits.SwapBytes(etag);

                using (table.Allocate(out TableValueBuilder tvb))
                {
                    tvb.Add(changeVectorSlice.Content.Ptr, changeVectorSlice.Size);
                    tvb.Add(lowerId);
                    tvb.Add(SpecialChars.RecordSeparator);
                    tvb.Add(newEtagSwapBytes);
                    tvb.Add(idSlice);
                    tvb.Add(document.BasePointer, document.Size);
                    tvb.Add((int)flags);
                    tvb.Add(NotDeletedRevisionMarker);
                    tvb.Add(lastModifiedTicks);
                    tvb.Add(context.GetTransactionMarker());
                    if (flags.Contain(DocumentFlags.Resolved))
                    {
                        tvb.Add((int)DocumentFlags.Resolved);
                    }
                    else
                    {
                        tvb.Add(0);
                    }
                    tvb.Add(Bits.SwapBytes(lastModifiedTicks));
                    table.Insert(tvb);
                }

                DeleteOldRevisions(context, table, lowerId, collectionName, configuration, nonPersistentFlags, changeVector, lastModifiedTicks);
            }
        }

        private BlittableJsonReaderObject RecreateCountersIfNeeded(DocumentsOperationContext context, string id, BlittableJsonReaderObject document)
        {
            if (document.TryGet(Constants.Documents.Metadata.Key, out BlittableJsonReaderObject metadata) &&
                metadata.TryGet(Constants.Documents.Metadata.Counters, out BlittableJsonReaderArray counterNames))
            {
                var dvj = new DynamicJsonValue();
                for (var i = 0; i < counterNames.Length; i++)
                {
                    var counter = counterNames[i].ToString();
                    var val = _documentsStorage.CountersStorage.GetCounterValue(context, id, counter);
                    if (val == null)
                        continue;
                    dvj[counter] = val.Value;
                }

                metadata.Modifications = new DynamicJsonValue(metadata)
                {
                    [Constants.Documents.Metadata.RevisionCounters] = dvj
                };
                metadata.Modifications.Remove(Constants.Documents.Metadata.Counters);
                document.Modifications = new DynamicJsonValue(document)
                {
                    [Constants.Documents.Metadata.Key] = metadata
                };

                document = context.ReadObject(document, id, BlittableJsonDocumentBuilder.UsageMode.ToDisk);
            }

            return document;
        }

        private void PutFromRevisionIfChangeVectorIsGreater(
            DocumentsOperationContext context,
            BlittableJsonReaderObject document,
            string id,
            string changeVector,
            long lastModifiedTicks,
            DocumentFlags flags,
            NonPersistentDocumentFlags nonPersistentFlags,
            CollectionName collectionName = null)
        {
            if (nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromReplication) == false)
                return;

            if ((flags.Contain(DocumentFlags.Revision) || flags.Contain(DocumentFlags.DeleteRevision)) == false)
                return; // only revision can overwrite the document

            if (flags.Contain(DocumentFlags.Conflicted))
                return; // but, conflicted revision can't

            using (DocumentIdWorker.GetLowerIdSliceAndStorageKey(context, id, out var lowerId, out _))
            {
                var conflictStatus = ConflictsStorage.GetConflictStatusForDocument(context, id, changeVector, out _, out _);
                if (conflictStatus != ConflictStatus.Update)
                    return; // Do not modify the document.

                if (flags.Contain(DocumentFlags.Resolved))
                {
                    _database.ReplicationLoader.ConflictResolver.SaveLocalAsRevision(context, id);
                }

                if (document == null)
                {
                    _documentsStorage.Delete(context, lowerId, id, null, lastModifiedTicks, changeVector, collectionName,
                        nonPersistentFlags | NonPersistentDocumentFlags.FromRevision);
                    return;
                }
                _documentsStorage.Put(context, id, null, document, lastModifiedTicks, changeVector,
                    flags.Strip(DocumentFlags.Revision), nonPersistentFlags | NonPersistentDocumentFlags.FromRevision);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DeleteOldRevisions(DocumentsOperationContext context, Table table, Slice lowerId, CollectionName collectionName,
            RevisionsCollectionConfiguration configuration, NonPersistentDocumentFlags nonPersistentFlags, string changeVector, long lastModifiedTicks)
        {
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            {
                // We delete the old revisions after we put the current one, 
                // because in case that MinimumRevisionsToKeep is 3 or lower we may get a revision document from replication
                // which is old. But because we put it first, we make sure to clean this document, because of the order to the revisions.
                var revisionsCount = IncrementCountOfRevisions(context, prefixSlice, 1);
                DeleteOldRevisions(context, table, prefixSlice, collectionName, configuration, revisionsCount, nonPersistentFlags, changeVector, lastModifiedTicks);
            }
        }

        private void DeleteOldRevisions(DocumentsOperationContext context, Table table, Slice prefixSlice, CollectionName collectionName,
            RevisionsCollectionConfiguration configuration, long revisionsCount, NonPersistentDocumentFlags nonPersistentFlags, string changeVector, long lastModifiedTicks)
        {
            if ((nonPersistentFlags & NonPersistentDocumentFlags.FromSmuggler) == NonPersistentDocumentFlags.FromSmuggler)
                return;

            if (configuration.MinimumRevisionsToKeep.HasValue == false &&
                configuration.MinimumRevisionAgeToKeep.HasValue == false)
                return;

            var numberOfRevisionsToDelete = revisionsCount - configuration.MinimumRevisionsToKeep ?? 0;
            if (numberOfRevisionsToDelete <= 0)
                return;

            var deletedRevisionsCount = DeleteRevisions(context, table, prefixSlice, collectionName, numberOfRevisionsToDelete, configuration.MinimumRevisionAgeToKeep, changeVector, lastModifiedTicks);
            Debug.Assert(numberOfRevisionsToDelete >= deletedRevisionsCount);
            IncrementCountOfRevisions(context, prefixSlice, -deletedRevisionsCount);
        }

        public void DeleteRevisionsFor(DocumentsOperationContext context, string id)
        {
            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            {
                var collectionName = GetCollectionFor(context, prefixSlice);
                if (collectionName == null)
                {
                    if (_logger.IsInfoEnabled)
                        _logger.Info($"Tried to delete all revisions for '{id}' but no revisions found.");
                    return;
                }

                var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);
                var newEtag = _documentsStorage.GenerateNextEtag();
                var changeVector = _documentsStorage.GetNewChangeVector(context, newEtag);
                context.LastDatabaseChangeVector = changeVector;
                var lastModifiedTicks = _database.Time.GetUtcNow().Ticks;
                DeleteRevisions(context, table, prefixSlice, collectionName, long.MaxValue, null, changeVector, lastModifiedTicks);
                DeleteCountOfRevisions(context, prefixSlice);
            }
        }

        public void DeleteRevisionsBefore(DocumentsOperationContext context, string collection, DateTime time)
        {
            var collectionName = new CollectionName(collection);
            var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);
            table.DeleteByPrimaryKey(Slices.BeforeAllKeys, deleted =>
            {
                var lastModified = TableValueToDateTime((int)RevisionsTable.LastModified, ref deleted.Reader);
                if (lastModified >= time)
                    return false;

                // We won't create tombstones here as it might create LOTS of tombstones 
                // with the same transaction marker and the same change vector.

                using (TableValueToSlice(context, (int)RevisionsTable.LowerId, ref deleted.Reader, out Slice lowerId))
                using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
                {
                    IncrementCountOfRevisions(context, prefixSlice, -1);
                }

                return true;
            });
        }

        private CollectionName GetCollectionFor(DocumentsOperationContext context, Slice prefixSlice)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            var tvr = table.SeekOneForwardFromPrefix(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice);
            if (tvr == null)
                return null;

            var ptr = tvr.Reader.Read((int)RevisionsTable.Document, out int size);
            var data = new BlittableJsonReaderObject(ptr, size, context);

            return _documentsStorage.ExtractCollectionName(context, data);
        }

        public IEnumerable<string> GetCollections(Transaction transaction)
        {
            using (var it = transaction.LowLevelTransaction.RootObjects.Iterate(false))
            {
                it.SetRequiredPrefix(RevisionsPrefix);

                if (it.Seek(RevisionsPrefix) == false)
                    yield break;

                do
                {
                    var collection = it.CurrentKey.ToString();
                    yield return collection.Substring(RevisionsPrefix.Size);
                }
                while (it.MoveNext());
            }
        }

        private long DeleteRevisions(DocumentsOperationContext context, Table table, Slice prefixSlice, CollectionName collectionName,
            long numberOfRevisionsToDelete, TimeSpan? minimumTimeToKeep, string changeVector, long lastModifiedTicks)
        {
            long maxEtagDeleted = 0;
            Table writeTable = null;
            string currentCollection = null;
            var deletedRevisionsCount = 0;

            while (true)
            {
                var hasValue = false;

                foreach (var read in table.SeekForwardFrom(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice, skip: 0, startsWith: true))
                {
                    if (numberOfRevisionsToDelete <= deletedRevisionsCount)
                        break;
                   
                    var tvr = read.Result.Reader;
                    var revision = TableValueToRevision(context, ref tvr);

                    if (minimumTimeToKeep.HasValue &&
                        _database.Time.GetUtcNow() - revision.LastModified <= minimumTimeToKeep.Value)
                        return deletedRevisionsCount;

                    hasValue = true;

                    using (Slice.From(context.Allocator, revision.ChangeVector, out var keySlice))
                    {
                        CreateTombstone(context, keySlice, revision.Etag, collectionName, changeVector, lastModifiedTicks);

                        maxEtagDeleted = Math.Max(maxEtagDeleted, revision.Etag);
                        if ((revision.Flags & DocumentFlags.HasAttachments) == DocumentFlags.HasAttachments)
                        {
                            _documentsStorage.AttachmentsStorage.DeleteRevisionAttachments(context, revision, changeVector, lastModifiedTicks);
                        }

                        var docCollection = CollectionName.GetCollectionName(revision.Data);
                        if (writeTable == null || docCollection != currentCollection)
                        {
                            currentCollection = docCollection;
                            writeTable = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, new CollectionName(docCollection));
                        }

                        writeTable.DeleteByKey(keySlice);
                    }
                    
                    deletedRevisionsCount++;
                    break;
                }

                if (hasValue == false)
                    break;
            }
            
            _database.DocumentsStorage.EnsureLastEtagIsPersisted(context, maxEtagDeleted);
            return deletedRevisionsCount;
        }

        public void DeleteRevision(DocumentsOperationContext context, Slice key, string collection, string changeVector, long lastModifiedTicks)
        {
            var collectionName = new CollectionName(collection);
            var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);

            long revisionEtag;
            if (table.ReadByKey(key, out TableValueReader tvr))
            {
                using (TableValueToSlice(context, (int)RevisionsTable.LowerId, ref tvr, out Slice lowerId))
                using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
                {
                    IncrementCountOfRevisions(context, prefixSlice, -1);
                }

                revisionEtag = TableValueToEtag((int)RevisionsTable.Etag, ref tvr);
                table.Delete(tvr.Id);
            }
            else
            {
                var tombstoneTable = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema, RevisionsTombstonesSlice);
                if (tombstoneTable.ReadByKey(key, out var existingTombstone))
                {
                    revisionEtag = TableValueToEtag((int)TombstoneTable.Etag, ref existingTombstone);
                    tombstoneTable.Delete(existingTombstone.Id);
                }
                else
                {
                    // we need to generate a unique etag if we got a tombstone revisions from replication, 
                    // but we don't want to mess up the order of events so the delete revision etag we use is negative
                    revisionEtag = _documentsStorage.GenerateNextEtagForReplicatedTombstoneMissingDocument(context);
                }
            }
            CreateTombstone(context, key, revisionEtag, collectionName, changeVector, lastModifiedTicks);
        }

        private void CreateTombstone(DocumentsOperationContext context, Slice keySlice, long revisionEtag,
            CollectionName collectionName, string changeVector, long lastModifiedTicks)
        {
            var newEtag = _documentsStorage.GenerateNextEtag();

            var table = context.Transaction.InnerTransaction.OpenTable(TombstonesSchema, RevisionsTombstonesSlice);
            if (table.VerifyKeyExists(keySlice))
                return; // revisions (and revisions tombstones) are immutable, we can safely ignore this 

            using (DocumentIdWorker.GetStringPreserveCase(context, collectionName.Name, out Slice collectionSlice))
            using (Slice.From(context.Allocator, changeVector, out var cv))
            using (table.Allocate(out TableValueBuilder tvb))
            {
                tvb.Add(keySlice.Content.Ptr, keySlice.Size);
                tvb.Add(Bits.SwapBytes(newEtag));
                tvb.Add(Bits.SwapBytes(revisionEtag));
                tvb.Add(context.GetTransactionMarker());
                tvb.Add((byte)Tombstone.TombstoneType.Revision);
                tvb.Add(collectionSlice);
                tvb.Add((int)DocumentFlags.None);
                tvb.Add(cv.Content.Ptr, cv.Size);
                tvb.Add(lastModifiedTicks);
                table.Set(tvb);
            }
        }

        private static long IncrementCountOfRevisions(DocumentsOperationContext context, Slice prefixedLowerId, long delta)
        {
            var numbers = context.Transaction.InnerTransaction.ReadTree(RevisionsCountSlice);
            return numbers.Increment(prefixedLowerId, delta);
        }

        private static void DeleteCountOfRevisions(DocumentsOperationContext context, Slice prefixedLowerId)
        {
            var numbers = context.Transaction.InnerTransaction.ReadTree(RevisionsCountSlice);
            numbers.Delete(prefixedLowerId);
        }

        public void Delete(DocumentsOperationContext context, string id, Slice lowerId, CollectionName collectionName, string changeVector,
            long lastModifiedTicks, NonPersistentDocumentFlags nonPersistentFlags, DocumentFlags flags)
        {
            using (DocumentIdWorker.GetStringPreserveCase(context, id, out Slice idPtr))
            {
                var deleteRevisionDocument = context.ReadObject(new DynamicJsonValue
                {
                    [Constants.Documents.Metadata.Key] = new DynamicJsonValue
                    {
                        [Constants.Documents.Metadata.Collection] = collectionName.Name
                    }
                }, "RevisionsBin");
                Delete(context, lowerId, idPtr, id, collectionName, deleteRevisionDocument, changeVector, lastModifiedTicks, nonPersistentFlags, flags);
            }
        }

        public void Delete(DocumentsOperationContext context, string id, BlittableJsonReaderObject deleteRevisionDocument,
            DocumentFlags flags, NonPersistentDocumentFlags nonPersistentFlags, string changeVector, long lastModifiedTicks)
        {
            BlittableJsonReaderObject.AssertNoModifications(deleteRevisionDocument, id, assertChildren: true);

            using (DocumentIdWorker.GetLowerIdSliceAndStorageKey(context, id, out Slice lowerId, out Slice idPtr))
            {
                var collectionName = _documentsStorage.ExtractCollectionName(context, deleteRevisionDocument);
                Delete(context, lowerId, idPtr, id, collectionName, deleteRevisionDocument, changeVector, lastModifiedTicks, nonPersistentFlags, flags);
            }
        }

        private void Delete(DocumentsOperationContext context, Slice lowerId, Slice idSlice, string id, CollectionName collectionName,
            BlittableJsonReaderObject deleteRevisionDocument, string changeVector,
            long lastModifiedTicks, NonPersistentDocumentFlags nonPersistentFlags, DocumentFlags flags)
        {
            Debug.Assert(changeVector != null, "Change vector must be set");

            if (flags.Contain(DocumentFlags.HasAttachments))
            {
                flags &= ~DocumentFlags.HasAttachments;
            }

            var fromReplication = nonPersistentFlags.Contain(NonPersistentDocumentFlags.FromReplication);

            var configuration = GetRevisionsConfiguration(collectionName.Name, flags);
            if (configuration.Disabled && fromReplication == false)
                return;

            var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);

            using (Slice.From(context.Allocator, changeVector, out var changeVectorSlice))
            {
                var revisionExists = table.ReadByKey(changeVectorSlice,out var tvr);
                if (revisionExists)
                {
                    MarkRevisionsAsConflictedIfNeeded(context, lowerId, idSlice, flags, tvr, table, changeVectorSlice);
                    return;
                }

                if (configuration.Disabled == false && configuration.PurgeOnDelete)
                {
                    using (GetKeyPrefix(context, lowerId, out var prefixSlice))
                    {
                        DeleteRevisions(context, table, prefixSlice, collectionName, long.MaxValue, null, changeVector, lastModifiedTicks);
                        DeleteCountOfRevisions(context, prefixSlice);
                    }
                    return;
                }

                PutFromRevisionIfChangeVectorIsGreater(context, null, id, changeVector, lastModifiedTicks, flags, nonPersistentFlags, collectionName);

                var newEtag = _database.DocumentsStorage.GenerateNextEtag();
                var newEtagSwapBytes = Bits.SwapBytes(newEtag);

                using (table.Allocate(out TableValueBuilder tvb))
                {
                    tvb.Add(changeVectorSlice.Content.Ptr, changeVectorSlice.Size);
                    tvb.Add(lowerId);
                    tvb.Add(SpecialChars.RecordSeparator);
                    tvb.Add(newEtagSwapBytes);
                    tvb.Add(idSlice);
                    tvb.Add(deleteRevisionDocument.BasePointer, deleteRevisionDocument.Size);
                    tvb.Add((int)(DocumentFlags.DeleteRevision | flags));
                    tvb.Add(newEtagSwapBytes);
                    tvb.Add(lastModifiedTicks);
                    tvb.Add(context.GetTransactionMarker());
                    if (flags.Contain(DocumentFlags.Resolved))
                    {
                        tvb.Add((int)DocumentFlags.Resolved);
                    }
                    else
                    {
                        tvb.Add(0);
                    }
                    tvb.Add(Bits.SwapBytes(lastModifiedTicks));
                    table.Insert(tvb);
                }

                DeleteOldRevisions(context, table, lowerId, collectionName, configuration, nonPersistentFlags, changeVector, lastModifiedTicks);
            }
        }

        private void MarkRevisionsAsConflictedIfNeeded(DocumentsOperationContext context, Slice lowerId, Slice idSlice, DocumentFlags flags, TableValueReader tvr, Table table,
            Slice changeVectorSlice)
        {
            // Revisions are immutable, but if there was a conflict we need to update the flags accordingly with the `Conflicted` flag.
            if (flags.Contain(DocumentFlags.Conflicted))
            {
                var currentFlags = TableValueToFlags((int)RevisionsTable.Flags, ref tvr);
                if (currentFlags.Contain(DocumentFlags.Conflicted) == false)
                {
                    MarkRevisionAsConflicted(context, tvr, table, changeVectorSlice, lowerId, idSlice);
                }
            }
        }

        private void MarkRevisionAsConflicted(DocumentsOperationContext context, TableValueReader tvr, Table table, Slice changeVectorSlice, Slice lowerId, Slice idSlice)
        {
            var revisionCopy = context.GetMemory(tvr.Size);
            // we have to copy it to the side because we might do a defrag during update, and that
            // can cause corruption if we read from the old value (which we just deleted)
            Memory.Copy(revisionCopy.Address, tvr.Pointer, tvr.Size);
            var copyTvr = new TableValueReader(revisionCopy.Address, tvr.Size);

            var revision = TableValueToRevision(context, ref copyTvr);
            var flags = revision.Flags | DocumentFlags.Conflicted;
            var newEtag = _database.DocumentsStorage.GenerateNextEtag();
            var deletedEtag = TableValueToEtag((int)RevisionsTable.DeletedEtag, ref tvr);
            var resolvedFlag = TableValueToFlags((int)RevisionsTable.Resolved, ref tvr);

            using (table.Allocate(out TableValueBuilder tvb))
            {
                tvb.Add(changeVectorSlice.Content.Ptr, changeVectorSlice.Size);
                tvb.Add(lowerId);
                tvb.Add(SpecialChars.RecordSeparator);
                tvb.Add(Bits.SwapBytes(newEtag));
                tvb.Add(idSlice);
                tvb.Add(revision.Data.BasePointer, revision.Data.Size);
                tvb.Add((int)flags);
                tvb.Add(deletedEtag);
                tvb.Add(revision.LastModified.Ticks);
                tvb.Add(context.GetTransactionMarker());
                tvb.Add((int)resolvedFlag);
                tvb.Add(Bits.SwapBytes(revision.LastModified.Ticks));
                table.Set(tvb);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ByteStringContext.InternalScope GetKeyPrefix(DocumentsOperationContext context, Slice lowerId, out Slice prefixSlice)
        {
            return GetKeyPrefix(context, lowerId.Content.Ptr, lowerId.Size, out prefixSlice);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ByteStringContext.InternalScope GetKeyPrefix(DocumentsOperationContext context, byte* lowerId, int lowerIdSize, out Slice prefixSlice)
        {
            var scope = context.Allocator.Allocate(lowerIdSize + 1, out ByteString keyMem);

            Memory.Copy(keyMem.Ptr, lowerId, lowerIdSize);
            keyMem.Ptr[lowerIdSize] = SpecialChars.RecordSeparator;

            prefixSlice = new Slice(SliceOptions.Key, keyMem);
            return scope;
        }

        private static ByteStringContext.InternalScope GetLastKey(DocumentsOperationContext context, Slice lowerId, out Slice prefixSlice)
        {
            return GetKeyWithEtag(context, lowerId, long.MaxValue, out prefixSlice);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ByteStringContext<ByteStringMemoryCache>.InternalScope GetKeyWithEtag(DocumentsOperationContext context, Slice lowerId, long etag, out Slice prefixSlice)
        {
            var scope = context.Allocator.Allocate(lowerId.Size + 1 + sizeof(long), out ByteString keyMem);

            Memory.Copy(keyMem.Ptr, lowerId.Content.Ptr, lowerId.Size);
            keyMem.Ptr[lowerId.Size] = SpecialChars.RecordSeparator;

            var maxValue = Bits.SwapBytes(etag);
            Memory.Copy(keyMem.Ptr + lowerId.Size + 1, (byte*)&maxValue, sizeof(long));

            prefixSlice = new Slice(SliceOptions.Key, keyMem);
            return scope;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ByteStringContext.InternalScope GetEtagAsSlice(DocumentsOperationContext context, long etag, out Slice slice)
        {
            var scope = context.Allocator.Allocate(sizeof(long), out var keyMem);
            var swapped = Bits.SwapBytes(etag);
            Memory.Copy(keyMem.Ptr, (byte*)&swapped, sizeof(long));
            slice = new Slice(SliceOptions.Key, keyMem);
            return scope;
        }

        private static long CountOfRevisions(DocumentsOperationContext context, Slice prefix)
        {
            var numbers = context.Transaction.InnerTransaction.ReadTree(RevisionsCountSlice);
            return numbers.Read(prefix)?.Reader.ReadLittleEndianInt64() ?? 0;
        }

        public Document GetRevisionBefore(DocumentsOperationContext context, string id, DateTime max)
        {
            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            using (GetLastKey(context, lowerId, out Slice lastKey))
            {
                // Here we assume a reasonable number of revisions and scan the entire history
                // This is because we want to handle out of order revisions from multiple nodes so the local etag
                // order is different than the last modified order
                Document result = null;
                var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
                foreach (var tvr in table.SeekBackwardFrom(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice, lastKey, 0))
                {
                    var document = TableValueToRevision(context, ref tvr.Result.Reader);
                    if (document.LastModified > max)
                        continue;
                    if (result == null ||
                        result.LastModified < document.LastModified)
                    {
                        result = document;
                    }
                }
                return result;
            }

        }

        public (Document[] Revisions, long Count) GetRevisions(DocumentsOperationContext context, string id, int start, int take)
        {
            using (DocumentIdWorker.GetSliceFromId(context, id, out Slice lowerId))
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            using (GetLastKey(context, lowerId, out Slice lastKey))
            {
                var revisions = GetRevisions(context, prefixSlice, lastKey, start, take).ToArray();
                var count = CountOfRevisions(context, prefixSlice);
                return (revisions, count);
            }
        }

        private IEnumerable<Document> GetRevisions(DocumentsOperationContext context, Slice prefixSlice, Slice lastKey, int start, int take)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            foreach (var tvr in table.SeekBackwardFrom(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice, lastKey, start))
            {
                if (take-- <= 0)
                    yield break;

                var document = TableValueToRevision(context, ref tvr.Result.Reader);
                yield return document;
            }
        }

        public void GetLatestRevisionsBinEntryEtag(DocumentsOperationContext context, long startEtag, out string latestChangeVector)
        {
            latestChangeVector = null;
            foreach (var entry in GetRevisionsBinEntries(context, startEtag, 1))
            {
                latestChangeVector = entry.ChangeVector;
            }
        }

        public IEnumerable<Document> GetRevisionsBinEntries(DocumentsOperationContext context, long startEtag, int take)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            using (GetEtagAsSlice(context, startEtag, out var slice))
            {
                foreach (var tvr in table.SeekBackwardFrom(RevisionsSchema.Indexes[DeleteRevisionEtagSlice], slice))
                {
                    if (take-- <= 0)
                        yield break;

                    var etag = TableValueToEtag((int)RevisionsTable.DeletedEtag, ref tvr.Result.Reader);
                    if (etag == NotDeletedRevisionMarker)
                        yield break;

                    using (TableValueToSlice(context, (int)RevisionsTable.LowerId, ref tvr.Result.Reader, out Slice lowerId))
                    {
                        if (IsRevisionsBinEntry(context, table, lowerId, etag) == false)
                            continue;
                    }

                    yield return TableValueToRevision(context, ref tvr.Result.Reader);
                }
            }
        }

        private bool IsRevisionsBinEntry(DocumentsOperationContext context, Table table, Slice lowerId, long revisionsBinEntryEtag)
        {
            using (GetKeyPrefix(context, lowerId, out Slice prefixSlice))
            using (GetLastKey(context, lowerId, out Slice lastKey))
            {
                var tvr = table.SeekOneBackwardFrom(RevisionsSchema.Indexes[IdAndEtagSlice], prefixSlice, lastKey);
                if (tvr == null)
                {
                    Debug.Assert(false, "Cannot happen.");
                    return true;
                }

                var etag = TableValueToEtag((int)RevisionsTable.Etag, ref tvr.Reader);
                var flags = TableValueToFlags((int)RevisionsTable.Flags, ref tvr.Reader);
                Debug.Assert(revisionsBinEntryEtag <= etag, "Revisions bin entry etag candidate cannot meet a bigger etag.");
                return (flags & DocumentFlags.DeleteRevision) == DocumentFlags.DeleteRevision && revisionsBinEntryEtag >= etag;
            }
        }

        public Document GetRevision(DocumentsOperationContext context, string changeVector)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);

            using (Slice.From(context.Allocator, changeVector, out var cv))
            {
                if (table.ReadByKey(cv, out TableValueReader tvr) == false)
                    return null;
                return TableValueToRevision(context, ref tvr);
            }
        }

        public IEnumerable<Document> GetRevisionsFrom(DocumentsOperationContext context, long etag, int take)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);

            foreach (var tvr in table.SeekForwardFrom(RevisionsSchema.FixedSizeIndexes[AllRevisionsEtagsSlice], etag, 0))
            {
                var document = TableValueToRevision(context, ref tvr.Reader);
                yield return document;

                if (take-- <= 0)
                    yield break;
            }
        }

        public IEnumerable<(Document previous, Document current)> GetRevisionsFrom(DocumentsOperationContext context, CollectionName collectionName, long etag, int take)
        {
            var table = EnsureRevisionTableCreated(context.Transaction.InnerTransaction, collectionName);
            var docsSchemaIndex = RevisionsSchema.Indexes[IdAndEtagSlice];

            foreach (var tvr in table.SeekForwardFrom(RevisionsSchema.FixedSizeIndexes[CollectionRevisionsEtagsSlice], etag, 0))
            {
                if (take-- <= 0)
                    break;
                var current = TableValueToRevision(context, ref tvr.Reader);

                using (docsSchemaIndex.GetSlice(context.Allocator, ref tvr.Reader, out var idAndEtag))
                using (Slice.External(context.Allocator, idAndEtag, idAndEtag.Size - sizeof(long), out var prefix))
                {
                    bool hasPrevious = false;
                    foreach (var prevTvr in table.SeekBackwardFrom(docsSchemaIndex, prefix, idAndEtag, 1))
                    {
                        var previous = TableValueToRevision(context, ref prevTvr.Result.Reader);
                        yield return (previous, current);
                        hasPrevious = true;
                        break;
                    }
                    if (hasPrevious)
                        continue;
                }

                yield return (null, current);
            }
        }

        private static Document TableValueToRevision(JsonOperationContext context, ref TableValueReader tvr)
        {
            var result = new Document
            {
                StorageId = tvr.Id,
                LowerId = TableValueToString(context, (int)RevisionsTable.LowerId, ref tvr),
                Id = TableValueToId(context, (int)RevisionsTable.Id, ref tvr),
                Etag = TableValueToEtag((int)RevisionsTable.Etag, ref tvr),
                LastModified = TableValueToDateTime((int)RevisionsTable.LastModified, ref tvr),
                Flags = TableValueToFlags((int)RevisionsTable.Flags, ref tvr),
                TransactionMarker = *(short*)tvr.Read((int)RevisionsTable.TransactionMarker, out int size),
                ChangeVector = TableValueToChangeVector(context, (int)RevisionsTable.ChangeVector, ref tvr)
            };

            var ptr = tvr.Read((int)RevisionsTable.Document, out size);
            result.Data = new BlittableJsonReaderObject(ptr, size, context);

            return result;
        }

        public static Document ParseRawDataSectionRevisionWithValidation(JsonOperationContext context, ref TableValueReader tvr, int expectedSize, out long etag)
        {
            var ptr = tvr.Read((int)RevisionsTable.Document, out var size);
            if (size > expectedSize || size <= 0)
                throw new ArgumentException("Data size is invalid, possible corruption when parsing BlittableJsonReaderObject", nameof(size));

            var result = new Document
            {
                StorageId = tvr.Id,
                LowerId = TableValueToString(context, (int)RevisionsTable.LowerId, ref tvr),
                Id = TableValueToId(context, (int)RevisionsTable.Id, ref tvr),
                Etag = etag = TableValueToEtag((int)RevisionsTable.Etag, ref tvr),
                Data = new BlittableJsonReaderObject(ptr, size, context),
                LastModified = TableValueToDateTime((int)RevisionsTable.LastModified, ref tvr),
                Flags = TableValueToFlags((int)RevisionsTable.Flags, ref tvr),
                TransactionMarker = *(short*)tvr.Read((int)RevisionsTable.TransactionMarker, out size),
                ChangeVector = TableValueToChangeVector(context, (int)RevisionsTable.ChangeVector, ref tvr)
            };

            if (size != sizeof(short))
                throw new ArgumentException("TransactionMarker size is invalid, possible corruption when parsing BlittableJsonReaderObject", nameof(size));

            return result;
        }


        private ByteStringContext.ExternalScope GetResolvedSlice(DocumentsOperationContext context, DateTime date, out Slice slice)
        {
            var size = sizeof(int) + sizeof(long);
            var mem = context.GetMemory(size);
            var flag = (int)DocumentFlags.Resolved;
            Memory.Copy(mem.Address, (byte*)&flag, sizeof(int));
            var ticks = Bits.SwapBytes(date.Ticks);
            Memory.Copy(mem.Address + sizeof(int), (byte*)&ticks, sizeof(long));
            return Slice.External(context.Allocator, mem.Address, size, out slice);
        }

        public IEnumerable<Document> GetResolvedDocumentsSince(DocumentsOperationContext context, DateTime since, int take = 1024)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            using (GetResolvedSlice(context, since, out var slice))
            {
                foreach (var item in table.SeekForwardFrom(RevisionsSchema.Indexes[ResolvedFlagByEtagSlice], slice, 0))
                {
                    if (take == 0)
                    {
                        yield break;
                    }
                    take--;
                    yield return TableValueToRevision(context, ref item.Result.Reader);
                }
            }
        }

        public long GetNumberOfRevisionDocuments(DocumentsOperationContext context)
        {
            var table = new Table(RevisionsSchema, context.Transaction.InnerTransaction);
            return table.GetNumberOfEntriesFor(RevisionsSchema.FixedSizeIndexes[AllRevisionsEtagsSlice]);
        }
    }
}
