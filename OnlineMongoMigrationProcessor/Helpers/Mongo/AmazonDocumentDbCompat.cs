using MongoDB.Bson;
using MongoDB.Driver;
using OnlineMongoMigrationProcessor.Context;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace OnlineMongoMigrationProcessor.Helpers.Mongo
{
    /// <summary>
    /// Amazon DocumentDB target support (fork addition).
    /// Detects Amazon DocumentDB endpoints, supplies the target CA bundle, and validates
    /// server certificates against a custom CA bundle such as the RDS global-bundle.pem.
    /// </summary>
    public static class AmazonDocumentDbCompat
    {
        private static readonly string[] InstanceClusterHostSuffixes = { ".docdb.amazonaws.com", ".docdb.amazonaws.com.cn" };
        private static readonly string[] ElasticClusterHostSuffixes = { ".docdb-elastic.amazonaws.com", ".docdb-elastic.amazonaws.com.cn" };

        // Clients created for an Amazon DocumentDB endpoint whose host name doesn't reveal it (SSH tunnel, custom DNS).
        private static readonly ConcurrentDictionary<MongoClient, bool> _markedClients = new();

        /// <summary>CA bundle (PEM) for target connections, e.g. the RDS global-bundle.pem. Set from MigrationSettings.</summary>
        public static string? TargetCaCertContents { get; set; }

        /// <summary>Treat the job's target as Amazon DocumentDB even when the host name doesn't show it.</summary>
        public static bool TreatTargetAsAmazonDocumentDb { get; set; }

        public static void ApplySettings(MigrationSettings settings)
        {
            if (settings == null) return;
            TargetCaCertContents = settings.CACertContentsForTargetServer;
            TreatTargetAsAmazonDocumentDb = settings.TreatTargetAsAmazonDocumentDB;
        }

        public static bool IsInstanceClusterHost(string? host) => HasSuffix(host, InstanceClusterHostSuffixes);

        public static bool IsElasticClusterHost(string? host) => HasSuffix(host, ElasticClusterHostSuffixes);

        public static bool IsAmazonDocumentDbHost(string? host) => IsInstanceClusterHost(host) || IsElasticClusterHost(host);

        public static bool IsAmazonDocumentDbConnectionString(string? connectionString) =>
            GetHosts(connectionString).Any(IsAmazonDocumentDbHost);

        /// <summary>True when the connection string is the target of a registered migration job.</summary>
        public static bool IsTargetConnectionString(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return false;
            try
            {
                return MigrationJobContext.TargetConnectionString.ContainsValue(connectionString);
            }
            catch (InvalidOperationException)
            {
                // Collection modified concurrently; the host-name check still applies.
                return false;
            }
        }

        /// <summary>
        /// True when a connection should be treated as Amazon DocumentDB: the host name is an
        /// Amazon DocumentDB endpoint, or it is the job target and the user enabled the setting.
        /// </summary>
        public static bool ShouldTreatAsAmazonDocumentDb(string? connectionString) =>
            IsAmazonDocumentDbConnectionString(connectionString)
            || (TreatTargetAsAmazonDocumentDb && IsTargetConnectionString(connectionString));

        public static void MarkClient(MongoClient client)
        {
            if (client != null) _markedClients[client] = true;
        }

        public static bool IsAmazonDocumentDbClient(MongoClient? client)
        {
            if (client == null) return false;
            if (_markedClients.ContainsKey(client)) return true;
            return client.Settings.Servers.Any(s => IsAmazonDocumentDbHost(s.Host));
        }

        /// <summary>Elastic clusters support hashed sharding; instance-based clusters don't shard at all.</summary>
        public static bool IsElasticClusterClient(MongoClient? client) =>
            client != null && client.Settings.Servers.Any(s => IsElasticClusterHost(s.Host));

        /// <summary>True when the connection string sets the given URI option (case-insensitive key match).</summary>
        public static bool HasUriOption(string? connectionString, string option) =>
            GetUriOptionValue(connectionString, option) != null;

        public static string? GetUriOptionValue(string? connectionString, string option)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return null;
            var parts = connectionString.Split(new[] { '?' }, 2);
            if (parts.Length < 2) return null;
            foreach (var pair in parts[1].Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (string.Equals(kv[0].Trim(), option, StringComparison.OrdinalIgnoreCase))
                    return kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Trim()) : string.Empty;
            }
            return null;
        }

        public static bool IsUriOptionTrue(string? connectionString, string option) =>
            string.Equals(GetUriOptionValue(connectionString, option), "true", StringComparison.OrdinalIgnoreCase);

        /// <summary>Removes the given URI options (case-insensitive) and keeps every other option verbatim.</summary>
        public static string RemoveUriOptions(string connectionString, params string[] options)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return connectionString;
            var parts = connectionString.Split(new[] { '?' }, 2);
            if (parts.Length < 2) return connectionString;
            var kept = parts[1]
                .Split('&')
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !options.Any(o => string.Equals(p.Split(new[] { '=' }, 2)[0].Trim(), o, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return kept.Count == 0 ? parts[0] : $"{parts[0]}?{string.Join("&", kept)}";
        }

        /// <summary>
        /// Appends retryWrites=false unless the connection string sets retryWrites explicitly.
        /// Amazon DocumentDB supports retryable writes only from engine 8.0.2.
        /// </summary>
        public static string EnsureRetryWritesSetting(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString) || HasUriOption(connectionString, "retryWrites"))
                return connectionString;
            if (connectionString.Contains('?'))
                return connectionString.EndsWith("?") ? $"{connectionString}retryWrites=false" : $"{connectionString}&retryWrites=false";

            // No query string: options must follow a path separator ("host:port/?..." or "host:port/db?...").
            int hostsStart = connectionString.IndexOf("://", StringComparison.Ordinal);
            bool hasPath = hostsStart >= 0 && connectionString.IndexOf('/', hostsStart + 3) >= 0;
            return hasPath ? $"{connectionString}?retryWrites=false" : $"{connectionString}/?retryWrites=false";
        }

        /// <summary>
        /// Compares the non-_id fields of a change event's documentKey (the source shard key) with the
        /// document already stored in the target under the same _id. Returns a description of the first
        /// difference, or null when the stored document has the same shard key (a harmless replay).
        /// Cosmos DB for MongoDB (RU) enforces _id uniqueness per shard key only, so two source documents
        /// can share an _id; an unsharded target keeps only one of them.
        /// </summary>
        public static string? FindShardKeyMismatch(BsonDocument? documentKey, BsonDocument? existingDocument)
        {
            if (documentKey == null || existingDocument == null) return null;
            foreach (var element in documentKey.Elements)
            {
                if (element.Name == "_id") continue;
                var existingValue = GetValueByPath(existingDocument, element.Name);
                if (existingValue == null || !existingValue.Equals(element.Value))
                {
                    var targetText = existingValue == null ? "<missing>" : existingValue.ToJson();
                    return $"{element.Name}: source={element.Value.ToJson()}, target={targetText}";
                }
            }
            return null;
        }

        public static BsonValue? GetValueByPath(BsonDocument document, string path)
        {
            if (document.TryGetValue(path, out var direct)) return direct;
            BsonValue current = document;
            foreach (var segment in path.Split('.'))
            {
                if (current is BsonDocument doc && doc.TryGetValue(segment, out var next))
                    current = next;
                else
                    return null;
            }
            return current;
        }

        /// <summary>
        /// Builds a validation callback that trusts only the CAs in the PEM bundle (custom root trust).
        /// Host name mismatches are rejected when <paramref name="enforceHostName"/> is true;
        /// <paramref name="allowInvalidCertificates"/> accepts any certificate (testing only).
        /// </summary>
        public static RemoteCertificateValidationCallback CreateCaBundleValidator(
            Log? log, string pem, bool enforceHostName, bool allowInvalidCertificates)
        {
            var caCerts = LoadPemCertificates(pem);
            if (caCerts.Count == 0)
                log?.WriteLine("The CA certificate (.pem) contains no certificates; TLS connections will be rejected.", LogType.Error);

            return (sender, certificate, chain, sslPolicyErrors) =>
            {
                try
                {
                    if (allowInvalidCertificates) return true;
                    if (certificate == null) return false;
                    if (enforceHostName && (sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                    {
                        log?.WriteLine($"Server certificate '{certificate.Subject}' doesn't match the host name. Connect with the cluster endpoint, or add tlsAllowInvalidHostnames=true (SSH tunnels).", LogType.Error);
                        return false;
                    }

                    using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    using var customChain = new X509Chain();
                    customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    customChain.ChainPolicy.CustomTrustStore.AddRange(caCerts);
                    customChain.ChainPolicy.ExtraStore.AddRange(caCerts);
                    if (chain != null)
                    {
                        // Intermediates sent by the server.
                        customChain.ChainPolicy.ExtraStore.AddRange(chain.ChainPolicy.ExtraStore);
                        foreach (var element in chain.ChainElements)
                            customChain.ChainPolicy.ExtraStore.Add(element.Certificate);
                    }
                    customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                    bool isValid = customChain.Build(leaf);
                    if (!isValid)
                    {
                        var status = string.Join("; ", customChain.ChainStatus.Select(s => s.StatusInformation.Trim()));
                        log?.WriteLine($"Server certificate '{certificate.Subject}' isn't trusted by the configured CA bundle: {status}", LogType.Error);
                    }
                    return isValid;
                }
                catch (Exception ex)
                {
                    log?.WriteLine($"Certificate validation failed: {ex}", LogType.Error);
                    return false;
                }
            };
        }

        public static X509Certificate2Collection LoadPemCertificates(string? pem)
        {
            var collection = new X509Certificate2Collection();
            if (string.IsNullOrWhiteSpace(pem)) return collection;

            const string header = "-----BEGIN CERTIFICATE-----";
            const string footer = "-----END CERTIFICATE-----";
            int start = 0;
            while ((start = pem.IndexOf(header, start, StringComparison.Ordinal)) != -1)
            {
                int end = pem.IndexOf(footer, start, StringComparison.Ordinal);
                if (end == -1) break;
                var base64 = pem.Substring(start + header.Length, end - start - header.Length)
                    .Replace("\r", string.Empty)
                    .Replace("\n", string.Empty)
                    .Trim();
                collection.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64)));
                start = end + footer.Length;
            }
            return collection;
        }

        private static bool HasSuffix(string? host, IEnumerable<string> suffixes) =>
            !string.IsNullOrWhiteSpace(host)
            && suffixes.Any(s => host.TrimEnd('.').EndsWith(s, StringComparison.OrdinalIgnoreCase));

        private static string[] GetHosts(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return Array.Empty<string>();
            var match = System.Text.RegularExpressions.Regex.Match(connectionString, @"^mongodb(?:\+srv)?://(?:[^@/]*@)?([^/?]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return Array.Empty<string>();
            return match.Groups[1].Value
                .Split(',')
                .Select(h => h.Trim())
                .Select(h => h.StartsWith("[") ? h : h.Split(':')[0])
                .Where(h => h.Length > 0)
                .ToArray();
        }
    }
}
