using System.IO;
using System.Reflection;

namespace Raven.Client
{
    public static class Constants
    {
        public class Json
        {

            private Json()
            {
            }

            public class Fields
            {
                private Fields()
                {
                }

                public const string Type = "$type";

                public const string Values = "$values";
            }
        }

        public class Headers
        {
            private Headers()
            {
            }

            public const string RequestTime = "Request-Time";

            public const string ServerStartupTime = "Server-Startup-Time";

            public const string RefreshTopology = "Refresh-Topology";

            public const string TopologyEtag = "Topology-Etag";

            public const string ClientConfigurationEtag = "Client-Configuration-Etag";

            public const string LastKnownClusterTransactionIndex = "Known-Raft-Index";

            public const string RefreshClientConfiguration = "Refresh-Client-Configuration";

            public const string Etag = "ETag";

            public const string ClientVersion = "Raven-Client-Version";

            public const string ServerVersion = "Raven-Server-Version";

            public const string IfNoneMatch = "If-None-Match";
            public const string TransferEncoding = "Transfer-Encoding";
            public const string ContentEncoding = "Content-Encoding";
            public const string ContentLength = "Content-Length";
        }

        public class Platform
        {
            private Platform()
            {
            }

            public class Windows
            {
                private Windows()
                {
                }

                public static readonly int MaxPath = (int)typeof(Path).GetField("MaxLongPath", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

                public static readonly string[] ReservedFileNames = { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9", "clock$" };
            }

            public class Linux
            {
                private Linux()
                {
                }

                public const int MaxPath = 4096;

                public const int MaxFileNameLength = 230;
            }
        }

        public class Certificates
        {
            private Certificates()
            {
            }

            public const string Prefix = "certificates/";
        }

        public class Configuration
        {
            private Configuration()
            {
            }

            public const string ClientId = "Configuration/Client";

            public const string StudioId = "Configuration/Studio";
        }

        public static class Counters
        {
            public const string All = "@all_counters";
        }

        public class Documents
        {
            private Documents()
            {
            }

            public const string Prefix = "db/";

            public const string UrlPrefix = "databases";

            public const int MaxDatabaseNameLength = 128;

            public enum SubscriptionChangeVectorSpecialStates
            {
                DoNotChange,
                LastDocument,
                BeginningOfTime
            }

            public class Metadata
            {
                private Metadata()
                {
                }

                public const string Collection = "@collection";

                public const string Projection = "@projection";

                public const string Key = "@metadata";

                public const string Id = "@id";

                public const string Conflict = "@conflict";

                public const string IdProperty = "Id";

                public const string Flags = "@flags";

                public const string Attachments = "@attachments";

                public const string Counters = "@counters";

                public const string RevisionCounters = "@counters-snapshot";

                public const string LegacyAttachmentsMetadata = "@legacy-attachment-metadata";

                public const string IndexScore = "@index-score";

                public const string LastModified = "@last-modified";

                public const string RavenClrType = "Raven-Clr-Type";

                public const string ChangeVector = "@change-vector";

                public const string Expires = "@expires";

                public const string HasValue = "HasValue";

                public const string Etag = "@etag";
            }

            public class Collections
            {
                public const string AllDocumentsCollection = "@all_docs";
            }

            public class Indexing
            {
                private Indexing()
                {
                }

                public const string SideBySideIndexNamePrefix = "ReplacementOf/";

                public class Fields
                {
                    private Fields()
                    {
                    }

                    public const string CountFieldName = "Count";

#if FEATURE_CUSTOM_SORTING
                    public const string CustomSortFieldName = "__customSort";
#endif

                    public const string DocumentIdFieldName = "id()";

                    public const string DocumentIdMethodName = "id";

                    public const string ReduceKeyHashFieldName = "hash(key())";

                    public const string ReduceKeyValueFieldName = "key()";

                    public const string AllFields = "__all_fields";

                    public const string AllStoredFields = "__all_stored_fields";

                    public const string SpatialShapeFieldName = "spatial(shape)";

                    internal const string RangeFieldSuffix = "_Range";

                    public const string RangeFieldSuffixLong = "_L" + RangeFieldSuffix;

                    public const string RangeFieldSuffixDouble = "_D" + RangeFieldSuffix;

                    public const string NullValue = "NULL_VALUE";

                    public const string EmptyString = "EMPTY_STRING";
                }

                public class Spatial
                {
                    private Spatial()
                    {
                    }

                    public const double DefaultDistanceErrorPct = 0.025d;

                    /// <summary>
                    /// The International Union of Geodesy and Geophysics says the Earth's mean radius in KM is:
                    ///
                    /// [1] http://en.wikipedia.org/wiki/Earth_radius
                    /// </summary>
                    public const double EarthMeanRadiusKm = 6371.0087714;

                    public const double MilesToKm = 1.60934;
                }
            }

            public class Querying
            {
                private Querying()
                {
                }

                public class Facet
                {
                    private Facet()
                    {
                    }

                    public const string AllResults = "@AllResults";
                }
            }

            public class Encryption
            {
                private Encryption()
                {
                }

                public const int DefaultGeneratedEncryptionKeyLength = 256 / 8;
            }

            public class PeriodicBackup
            {
                public const string FullBackupExtension = ".ravendb-full-backup";

                public const string SnapshotExtension = ".ravendb-snapshot";

                public const string IncrementalBackupExtension = ".ravendb-incremental-backup";

                public class Folders
                {
                    public const string Indexes = "Indexes";

                    public const string Documents = "Documents";

                    public const string Configuration = "Configuration";
                }
            }
        }

        internal class Monitoring
        {
            private Monitoring()
            {
            }

            internal class Snmp
            {
                private Snmp()
                {
                }

                public const string DatabasesMappingKey = "monitoring/snmp/databases/mapping";
            }
        }
    }
}
