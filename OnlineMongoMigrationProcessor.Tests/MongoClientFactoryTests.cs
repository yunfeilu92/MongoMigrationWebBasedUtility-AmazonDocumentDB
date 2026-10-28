using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OnlineMongoMigrationProcessor.Helpers.Mongo;
using Xunit;

namespace OnlineMongoMigrationProcessor.Tests
{
    public class MongoClientFactoryTests : IDisposable
    {
        private const string DocDbHost = "app.cluster-abc123.us-east-1.docdb.amazonaws.com";

        public MongoClientFactoryTests()
        {
            AmazonDocumentDbCompat.TargetCaCertContents = null;
            AmazonDocumentDbCompat.TreatTargetAsAmazonDocumentDb = false;
        }

        public void Dispose()
        {
            AmazonDocumentDbCompat.TargetCaCertContents = null;
            AmazonDocumentDbCompat.TreatTargetAsAmazonDocumentDb = false;
        }

        [Fact]
        public void Amazon_DocumentDB_clients_disable_retryable_writes_by_default()
        {
            var client = MongoClientFactory.Create(null, $"mongodb://u:p@{DocDbHost}:27017/?tls=true&replicaSet=rs0&readPreference=secondaryPreferred");
            Assert.False(client.Settings.RetryWrites);
            Assert.True(AmazonDocumentDbCompat.IsAmazonDocumentDbClient(client));
        }

        [Fact]
        public void Explicit_retryWrites_is_respected()
        {
            var client = MongoClientFactory.Create(null, $"mongodb://u:p@{DocDbHost}:27017/?tls=true&replicaSet=rs0&retryWrites=true");
            Assert.True(client.Settings.RetryWrites);
        }

        [Fact]
        public void Non_DocumentDB_clients_keep_driver_defaults()
        {
            var client = MongoClientFactory.Create(null, "mongodb://acct:key@acct.mongo.cosmos.azure.com:10255/?ssl=true&replicaSet=globaldb&retrywrites=false&maxIdleTimeMS=120000");
            Assert.Null(client.Settings.SslSettings?.ServerCertificateValidationCallback);
            Assert.False(AmazonDocumentDbCompat.IsAmazonDocumentDbClient(client));
        }

        [Fact]
        public void Target_CA_is_applied_to_Amazon_DocumentDB_connections()
        {
            AmazonDocumentDbCompat.TargetCaCertContents = TestCertificates.CreateRoot("CN=Factory Test Root").ExportCertificatePem();
            var client = MongoClientFactory.Create(null, $"mongodb://u:p@{DocDbHost}:27017/?tls=true&replicaSet=rs0&tlsCAFile=global-bundle.pem");
            Assert.NotNull(client.Settings.SslSettings?.ServerCertificateValidationCallback);
        }

        [Fact]
        public void Target_CA_is_not_applied_to_other_connections()
        {
            AmazonDocumentDbCompat.TargetCaCertContents = TestCertificates.CreateRoot("CN=Factory Test Root 2").ExportCertificatePem();
            var client = MongoClientFactory.Create(null, "mongodb://acct:key@acct.mongo.cosmos.azure.com:10255/?ssl=true&replicaSet=globaldb&appName=@acct@");
            Assert.Null(client.Settings.SslSettings?.ServerCertificateValidationCallback);
        }
    }

    public class CaBundleValidatorTests
    {
        private const string Host = "app.cluster-abc123.us-east-1.docdb.amazonaws.com";

        [Fact]
        public void Accepts_a_certificate_issued_by_a_bundle_CA()
        {
            using var root = TestCertificates.CreateRoot("CN=Bundle Root");
            using var leaf = TestCertificates.CreateLeaf(root, $"CN={Host}");
            var validate = AmazonDocumentDbCompat.CreateCaBundleValidator(null, root.ExportCertificatePem(), enforceHostName: true, allowInvalidCertificates: false);

            using var chain = new X509Chain();
            Assert.True(validate(new object(), leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors));
        }

        [Fact]
        public void Rejects_a_certificate_from_a_CA_outside_the_bundle()
        {
            using var trusted = TestCertificates.CreateRoot("CN=Trusted Root");
            using var other = TestCertificates.CreateRoot("CN=Other Root");
            using var leaf = TestCertificates.CreateLeaf(other, $"CN={Host}");
            var validate = AmazonDocumentDbCompat.CreateCaBundleValidator(null, trusted.ExportCertificatePem(), enforceHostName: true, allowInvalidCertificates: false);

            using var chain = new X509Chain();
            Assert.False(validate(new object(), leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors));
        }

        [Fact]
        public void Rejects_a_self_signed_server_certificate()
        {
            using var trusted = TestCertificates.CreateRoot("CN=Trusted Root 2");
            using var selfSigned = TestCertificates.CreateRoot($"CN={Host}");
            var validate = AmazonDocumentDbCompat.CreateCaBundleValidator(null, trusted.ExportCertificatePem(), enforceHostName: false, allowInvalidCertificates: false);

            using var chain = new X509Chain();
            Assert.False(validate(new object(), selfSigned, chain, SslPolicyErrors.RemoteCertificateChainErrors));
        }

        [Fact]
        public void Host_name_mismatch_is_rejected_only_when_enforced()
        {
            using var root = TestCertificates.CreateRoot("CN=Hostname Root");
            using var leaf = TestCertificates.CreateLeaf(root, $"CN={Host}");
            string pem = root.ExportCertificatePem();
            using var chain = new X509Chain();

            var strict = AmazonDocumentDbCompat.CreateCaBundleValidator(null, pem, enforceHostName: true, allowInvalidCertificates: false);
            Assert.False(strict(new object(), leaf, chain, SslPolicyErrors.RemoteCertificateNameMismatch));

            var tunnel = AmazonDocumentDbCompat.CreateCaBundleValidator(null, pem, enforceHostName: false, allowInvalidCertificates: false);
            Assert.True(tunnel(new object(), leaf, chain, SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
        }

        [Fact]
        public void Bundle_with_several_certificates_is_loaded()
        {
            using var a = TestCertificates.CreateRoot("CN=Root A");
            using var b = TestCertificates.CreateRoot("CN=Root B");
            var bundle = a.ExportCertificatePem() + "\n" + b.ExportCertificatePem();
            Assert.Equal(2, AmazonDocumentDbCompat.LoadPemCertificates(bundle).Count);

            using var leaf = TestCertificates.CreateLeaf(b, $"CN={Host}");
            var validate = AmazonDocumentDbCompat.CreateCaBundleValidator(null, bundle, enforceHostName: true, allowInvalidCertificates: false);
            using var chain = new X509Chain();
            Assert.True(validate(new object(), leaf, chain, SslPolicyErrors.RemoteCertificateChainErrors));
        }
    }

    internal static class TestCertificates
    {
        public static X509Certificate2 CreateRoot(string subject)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        }

        public static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string subject)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F; // positive serial number
            return request.Create(issuer, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddMonths(6), serial);
        }
    }
}
