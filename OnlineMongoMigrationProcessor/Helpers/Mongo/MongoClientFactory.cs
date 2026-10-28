using MongoDB.Driver;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace OnlineMongoMigrationProcessor.Helpers.Mongo
{
    public static class MongoClientFactory
    {
        // Cache MongoClient instances by a key derived from connection string + options
        // to avoid exhausting logical sessions on the server (TooManyLogicalSessions)
        private static readonly ConcurrentDictionary<string, MongoClient> _clientCache = new();

        public static MongoClient Create(Log? log,string connectionString, bool ReadConcernMajority=false, string? PEMFileContents=null)
        {
            
            var cleanConnStr = RemovePemPathFromConnectionString(connectionString);

            // Amazon DocumentDB target support: target connections (and any Amazon DocumentDB endpoint)
            // use the target CA bundle from Settings, or the file referenced by tlsCAFile, because the
            // .NET driver ignores tlsCAFile in the connection string.
            bool isTarget = AmazonDocumentDbCompat.IsTargetConnectionString(connectionString);
            bool isAmazonDocDb = AmazonDocumentDbCompat.ShouldTreatAsAmazonDocumentDb(connectionString);
            bool usesTargetCa = false;
            string? pem = PEMFileContents;
            if (string.IsNullOrWhiteSpace(pem) && (isTarget || isAmazonDocDb))
            {
                pem = AmazonDocumentDbCompat.TargetCaCertContents;
                if (string.IsNullOrWhiteSpace(pem))
                    pem = ReadCaFileFromConnectionString(log, cleanConnStr);
                usesTargetCa = !string.IsNullOrWhiteSpace(pem);
            }

            bool allowInvalidHostnames = AmazonDocumentDbCompat.IsUriOptionTrue(cleanConnStr, "tlsAllowInvalidHostnames");
            bool allowInvalidCertificates = AmazonDocumentDbCompat.IsUriOptionTrue(cleanConnStr, "tlsAllowInvalidCertificates");
            if (!string.IsNullOrWhiteSpace(pem))
            {
                // Handled by the CA bundle validator instead of the driver.
                cleanConnStr = AmazonDocumentDbCompat.RemoveUriOptions(cleanConnStr, "tlsCAFile", "tlsAllowInvalidHostnames", "tlsAllowInvalidCertificates");
            }

            string pemKey = string.IsNullOrWhiteSpace(pem) ? "none" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pem)));
            string cacheKey = $"{cleanConnStr}|{ReadConcernMajority}|{pemKey}|{usesTargetCa}|{isAmazonDocDb}";

            return _clientCache.GetOrAdd(cacheKey, _ =>
            {
                var settings = MongoClientSettings.FromUrl(new MongoUrl(cleanConnStr));

                if (!string.IsNullOrWhiteSpace(pem))
                {
                    // Only CAs from the bundle are trusted. The target CA also enforces the host name;
                    // a source CA keeps the upstream behavior of not checking it (e.g. connecting by IP).
                    settings.SslSettings = new SslSettings
                    {
                        ServerCertificateValidationCallback = AmazonDocumentDbCompat.CreateCaBundleValidator(
                            log, pem!, enforceHostName: usesTargetCa && !allowInvalidHostnames, allowInvalidCertificates)
                    };
                }

                if (isAmazonDocDb && !AmazonDocumentDbCompat.HasUriOption(cleanConnStr, "retryWrites"))
                {
                    // Amazon DocumentDB supports retryable writes only from engine 8.0.2.
                    settings.RetryWrites = false;
                }

                if (ReadConcernMajority)
                {
                    settings.ReadConcern = ReadConcern.Majority;
                }

                var client = new MongoClient(settings);
                if (isAmazonDocDb)
                {
                    AmazonDocumentDbCompat.MarkClient(client);
                }
                return client;
            });
        }

        private static string? ReadCaFileFromConnectionString(Log? log, string connectionString)
        {
            var caFile = AmazonDocumentDbCompat.GetUriOptionValue(connectionString, "tlsCAFile");
            if (string.IsNullOrWhiteSpace(caFile)) return null;
            if (!File.Exists(caFile))
            {
                log?.WriteLine($"tlsCAFile '{caFile}' doesn't exist on this host; upload the target CA certificate in Settings instead.", LogType.Warning);
                return null;
            }
            return File.ReadAllText(caFile);
        }

        private static string RemovePemPathFromConnectionString(string connStr)
        {
            
            string baseConnStr=string.Empty;
            string queryString = string.Empty;

            var parts = connStr.Split('?');
            if(parts.Length >= 1)
                baseConnStr= parts[0];
            if(parts.Length >=2)
                queryString = parts[1];

            if(string.IsNullOrWhiteSpace(queryString))
                return baseConnStr;

            var query = HttpUtility.ParseQueryString(queryString);
            query.Remove("pemPath");

            var keys = query.AllKeys ?? Array.Empty<string>();
            var newQuery = string.Join("&", keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k =>
                    $"{HttpUtility.UrlEncode((k ?? string.Empty).Trim())}={HttpUtility.UrlEncode((query[k] ?? string.Empty).Trim())}"));


            return string.IsNullOrWhiteSpace(newQuery) ? baseConnStr : $"{baseConnStr}?{newQuery}";
        }
    }
}
