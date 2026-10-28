using MongoDB.Bson;
using OnlineMongoMigrationProcessor.Helpers.Mongo;
using Xunit;

namespace OnlineMongoMigrationProcessor.Tests
{
    public class AmazonDocumentDbCompatTests
    {
        [Theory]
        [InlineData("mongodb://user:pw@app.cluster-abc123.us-east-1.docdb.amazonaws.com:27017/?tls=true&replicaSet=rs0", true)]
        [InlineData("mongodb://user:pw@app.cluster-ro-abc123.cn-north-1.docdb.amazonaws.com.cn:27017/?tls=true", true)]
        [InlineData("mongodb://user:pw@app-123456789012.us-east-1.docdb-elastic.amazonaws.com:27017/?tls=true", true)]
        [InlineData("mongodb://a.cluster-x.us-east-1.docdb.amazonaws.com:27017,b.cluster-x.us-east-1.docdb.amazonaws.com:27017/?replicaSet=rs0", true)]
        [InlineData("mongodb://acct:key@acct.mongo.cosmos.azure.com:10255/?ssl=true&replicaSet=globaldb", false)]
        [InlineData("mongodb+srv://user:pw@cluster0.mongocluster.cosmos.azure.com/?tls=true", false)]
        [InlineData("mongodb://user:pw@localhost:27017/?tls=true&tlsAllowInvalidHostnames=true", false)]
        [InlineData("mongodb://evil.docdb.amazonaws.com.attacker.example:27017/", false)]
        [InlineData("mongodb://host.example:27017/?appName=x.docdb.amazonaws.com", false)]
        [InlineData("", false)]
        public void Detects_Amazon_DocumentDB_connection_strings(string connectionString, bool expected)
        {
            Assert.Equal(expected, AmazonDocumentDbCompat.IsAmazonDocumentDbConnectionString(connectionString));
        }

        [Fact]
        public void Distinguishes_elastic_and_instance_cluster_hosts()
        {
            Assert.True(AmazonDocumentDbCompat.IsInstanceClusterHost("app.cluster-abc.us-east-1.docdb.amazonaws.com"));
            Assert.False(AmazonDocumentDbCompat.IsElasticClusterHost("app.cluster-abc.us-east-1.docdb.amazonaws.com"));
            Assert.True(AmazonDocumentDbCompat.IsElasticClusterHost("app-1.us-east-1.docdb-elastic.amazonaws.com"));
            Assert.False(AmazonDocumentDbCompat.IsInstanceClusterHost("app-1.us-east-1.docdb-elastic.amazonaws.com"));
        }

        [Theory]
        [InlineData("mongodb://h:27017", "mongodb://h:27017/?retryWrites=false")]
        [InlineData("mongodb://h:27017/", "mongodb://h:27017/?retryWrites=false")]
        [InlineData("mongodb://h:27017/admin", "mongodb://h:27017/admin?retryWrites=false")]
        [InlineData("mongodb://h:27017/?tls=true", "mongodb://h:27017/?tls=true&retryWrites=false")]
        [InlineData("mongodb://h:27017/?", "mongodb://h:27017/?retryWrites=false")]
        [InlineData("mongodb://h:27017/?tls=true&RetryWrites=true", "mongodb://h:27017/?tls=true&RetryWrites=true")]
        public void EnsureRetryWritesSetting_appends_only_when_missing(string input, string expected)
        {
            Assert.Equal(expected, AmazonDocumentDbCompat.EnsureRetryWritesSetting(input));
        }

        [Fact]
        public void Uri_options_are_matched_case_insensitively_and_decoded()
        {
            const string cs = "mongodb://h:27017/?TLSCAFile=%2Fopt%2Fcerts%2Fglobal-bundle.pem&tlsAllowInvalidHostnames=TRUE";
            Assert.Equal("/opt/certs/global-bundle.pem", AmazonDocumentDbCompat.GetUriOptionValue(cs, "tlsCAFile"));
            Assert.True(AmazonDocumentDbCompat.IsUriOptionTrue(cs, "tlsallowinvalidhostnames"));
            Assert.Null(AmazonDocumentDbCompat.GetUriOptionValue(cs, "replicaSet"));
            Assert.Equal("mongodb://h:27017/", AmazonDocumentDbCompat.RemoveUriOptions(cs, "tlsCAFile", "tlsAllowInvalidHostnames"));
            Assert.Equal("mongodb://h:27017/?tls=true", AmazonDocumentDbCompat.RemoveUriOptions("mongodb://h:27017/?tls=true&tlsCAFile=x", "TLSCAFILE"));
        }

        [Fact]
        public void Same_shard_key_is_a_replay_not_a_collision()
        {
            var key = new BsonDocument { { "tenantId", "t1" }, { "_id", 1 } };
            var existing = new BsonDocument { { "_id", 1 }, { "tenantId", "t1" }, { "v", 2 } };
            Assert.Null(AmazonDocumentDbCompat.FindShardKeyMismatch(key, existing));
        }

        [Fact]
        public void Different_shard_key_with_same_id_is_a_collision()
        {
            var key = new BsonDocument { { "tenantId", "t2" }, { "_id", 1 } };
            var existing = new BsonDocument { { "_id", 1 }, { "tenantId", "t1" } };
            var mismatch = AmazonDocumentDbCompat.FindShardKeyMismatch(key, existing);
            Assert.NotNull(mismatch);
            Assert.Contains("tenantId", mismatch);
        }

        [Fact]
        public void Nested_and_missing_shard_key_fields_are_compared()
        {
            var nestedKey = new BsonDocument { { "owner.id", 7 }, { "_id", "a" } };
            Assert.Null(AmazonDocumentDbCompat.FindShardKeyMismatch(nestedKey,
                new BsonDocument { { "_id", "a" }, { "owner", new BsonDocument("id", 7) } }));
            Assert.NotNull(AmazonDocumentDbCompat.FindShardKeyMismatch(nestedKey,
                new BsonDocument { { "_id", "a" }, { "owner", new BsonDocument("id", 8) } }));
            Assert.NotNull(AmazonDocumentDbCompat.FindShardKeyMismatch(nestedKey, new BsonDocument("_id", "a")));
        }

        [Fact]
        public void Id_only_document_key_is_never_a_collision()
        {
            Assert.Null(AmazonDocumentDbCompat.FindShardKeyMismatch(new BsonDocument("_id", 1), new BsonDocument("_id", 1)));
            Assert.Null(AmazonDocumentDbCompat.FindShardKeyMismatch(null, new BsonDocument("_id", 1)));
        }
    }
}
