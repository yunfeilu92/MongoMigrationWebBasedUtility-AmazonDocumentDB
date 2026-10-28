# Amazon DocumentDB target support

This fork of [AzureCosmosDB/MongoMigrationWebBasedUtility](https://github.com/AzureCosmosDB/MongoMigrationWebBasedUtility) adds **Amazon DocumentDB** as a migration target. The main use case is an online migration from **Azure Cosmos DB for MongoDB (RU)** to Amazon DocumentDB with minimal downtime.

> **Status:** the code compiles and the unit tests pass in GitHub Actions. The fork has **not yet been validated end to end against a live Amazon DocumentDB cluster.** Run a pilot migration before any production cutover.
>
> This is a community fork. It isn't affiliated with or supported by Microsoft or AWS.

## 中文说明

本 fork 在微软开源迁移工具的基础上，增加了以 **Amazon DocumentDB** 作为迁移目标端的支持，主要场景是 **Cosmos DB for MongoDB（RU）在线迁移到 Amazon DocumentDB**。

- 目标端 TLS：可以在 Settings 上传 `global-bundle.pem`，也可以在目标端连接串里直接写 `tlsCAFile=/app/certs/global-bundle.pem`（Docker 镜像已内置这个证书包）
- 连接 Amazon DocumentDB 时自动关闭 retryable writes，跳过不支持的分片命令，按 DocumentDB 的方式建索引
- 修复了 RU 复制中先保存位点、后写数据的问题，并且会把跨分片键的 `_id` 冲突记为错误，不再静默跳过
- **Cosmos RU 的 change stream 不产生删除事件**，迁移期间源端的删除不会同步过去，需要另行处理（见下文）
- 目前只通过了编译和单元测试，**还没有在真实的 Amazon DocumentDB 上做过端到端验证**

## What this fork changes

| Area | Upstream | This fork |
|---|---|---|
| TLS to the target | No target CA option. The .NET driver ignores `tlsCAFile`, so a private CA such as the Amazon RDS CA isn't trusted. | Target CA from **Settings → CA certificate file for target server**, or from the file named by `tlsCAFile` in the target connection string. It applies to target connections, any `*.docdb.amazonaws.com` endpoint, `mongorestore`, and the state store. |
| Certificate validation | Any certificate chain passes (`AllowUnknownCertificateAuthority`), and host names aren't checked. | Only CAs in the configured bundle are trusted (custom root trust). Target connections also check the host name unless `tlsAllowInvalidHostnames=true` is set (SSH tunnels). |
| Retryable writes | Driver default (on). | Off for Amazon DocumentDB unless `retryWrites` is set explicitly. Amazon DocumentDB supports retryable writes from engine 8.0.2 only. |
| Sharding | Runs `shardCollection` and `moveCollection` on the target. | Skips `shardCollection` on instance-based clusters and `moveCollection` on Amazon DocumentDB. Elastic clusters still get `shardCollection`. |
| Index builds | Sends the Azure-only `blocking` option with `maxTimeMS: 5000`, and treats timeouts as success. | Amazon DocumentDB gets a plain `createIndexes` call that waits for the build to finish. |
| RU copy checkpoint | One branch saves the resume token before writing the pending changes. | Writes first, then saves the token. |
| Duplicate keys on insert | Counted as skipped. | Same, except that an **`_id` collision across shard keys** is logged as an error (`_id collision in ...`) and counted as a failure. |
| Docker image | — | Includes the Amazon RDS CA bundle at `/app/certs/global-bundle.pem` (build arg `INSTALL_RDS_CA_BUNDLE=false` leaves it out). |

## Supported scenarios

| Source → target | Migration tool in the job | Deletes during the online phase |
|---|---|---|
| Cosmos DB for MongoDB (RU) → Amazon DocumentDB | **MongoDB Driver (Cosmos DB RU read optimized)**, online | **Not replicated.** Cosmos DB RU change streams don't emit delete events. See [Deletes](#deletes-cosmos-db-ru-source). |
| Azure DocumentDB (vCore) → Amazon DocumentDB | MongoDB Driver, online | Replicated; Azure DocumentDB change streams include deletes. Not validated yet. |
| MongoDB → Amazon DocumentDB | As upstream | As upstream |

## Deploy on AWS

The web app runs as a Linux container. On Linux, upstream requires a MongoDB-compatible **state store** for job metadata, which can be the target Amazon DocumentDB cluster. The app creates a database named `mongomigrationwebappstorage` there.

Prerequisites:

1. An Amazon DocumentDB cluster. An instance-based cluster is recommended.
2. An EC2 instance (or ECS service) in the same VPC, with Docker installed. Its security group must be allowed by the cluster's security group on port 27017.
3. Outbound access from the VPC to the source, for example through a NAT gateway. Add the NAT gateway's public IP to the Cosmos DB firewall.

Build and run:

```bash
git clone https://github.com/yunfeilu92/MongoMigrationWebBasedUtility-AmazonDocumentDB.git
cd MongoMigrationWebBasedUtility-AmazonDocumentDB
docker build -f MongoMigrationWebApp/Dockerfile -t mongo-migration .

# The container runs as UID 1654 and needs a writable data directory.
sudo mkdir -p /data/migration && sudo chown 1654:1654 /data/migration

docker run -d --name mongo-migration -p 127.0.0.1:8080:8080 \
  -e ResourceDrive=/app/migration-data \
  -e StateStoreAppID=migration01 \
  -e StateStoreConnectionStringOrPath='mongodb://<user>:<password>@<cluster-endpoint>:27017/?tls=true&tlsCAFile=/app/certs/global-bundle.pem&replicaSet=rs0&retryWrites=false' \
  -v /data/migration:/app/migration-data \
  mongo-migration
```

The web UI is protected by a single application password. Don't expose port 8080 to the internet. Reach it through Session Manager port forwarding instead:

```bash
aws ssm start-session --target <instance-id> \
  --document-name AWS-StartPortForwardingSession \
  --parameters portNumber=8080,localPortNumber=8080
```

Then open `http://localhost:8080`.

## Configure a migration job

**Target connection string**

```text
mongodb://<user>:<password>@<cluster-endpoint>:27017/?tls=true&tlsCAFile=/app/certs/global-bundle.pem&replicaSet=rs0&readPreference=primary
```

- `retryWrites=false` is added automatically for `*.docdb.amazonaws.com` hosts.
- Instead of `tlsCAFile`, you can upload `global-bundle.pem` in **Settings → CA certificate file for target server**. Download it from https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem.
- If you connect through an SSH tunnel, use `localhost` with `tlsAllowInvalidHostnames=true&directConnection=true`, and enable **Settings → Target is Amazon DocumentDB**.

**Source connection string (Cosmos DB RU)**

Use the connection string from the Azure portal (`*.mongo.cosmos.azure.com:10255`). Enable **Server Side Retry** on the Cosmos DB account, and raise the RU/s for the duration of the migration.

**Job settings**

- Migration tool: **MongoDB Driver (Cosmos DB RU read optimized)**, online mode.
- Keep the target collections unsharded on instance-based clusters.
- The RU-optimized mode doesn't create indexes. Create them on the target before you start, for example with the [Amazon DocumentDB Index Tool](https://github.com/awslabs/amazon-documentdb-tools/tree/master/index-tool).

## Cosmos DB RU checklist

### Deletes (Cosmos DB RU source)

Cosmos DB for MongoDB (RU) change streams only emit inserts, updates, and replaces ([Microsoft docs](https://learn.microsoft.com/en-us/azure/cosmos-db/mongodb/change-streams)). Documents deleted on the source after the copy starts stay on the target. Pick one of these approaches:

- **Soft deletes**: during the migration, the application sets `deleted: true` and a TTL instead of deleting. The update replicates, and you remove the flagged documents on the target.
- **Delete log**: the application also inserts the `_id` of each deleted document into a separate collection, and you replay those deletes on the target.
- **Final reconciliation**: during the cutover window, compare the `_id` sets on both sides and delete what is missing on the source. The time this takes depends on the document count, so measure it in a rehearsal.

### `_id` uniqueness

Cosmos DB RU enforces `_id` uniqueness **per shard key** only ([Microsoft docs](https://learn.microsoft.com/en-us/azure/cosmos-db/mongodb/indexing)). An unsharded Amazon DocumentDB collection keeps one document per `_id`. Scan sharded source collections for duplicate `_id` values before you migrate. During the migration, search the logs for `_id collision in`.

### Throttling

A throttled Cosmos DB change stream returns an empty page instead of an error, so lag can build up unnoticed. Watch the Cosmos DB 429 (16500) metrics during the migration.

### Partition splits

If a physical partition splits during the copy, the job stops with `New partitions found during copy process`. Pause the job and resume it.

### Validation

Use the tool's comparison, which hashes sampled documents with SHA-256. For a full comparison, use the [Amazon DocumentDB data differ](https://github.com/awslabs/amazon-documentdb-tools/tree/master/migration/data-differ). Its `--check-target` option finds documents that exist only on the target.

## Rollback with SyncBack

SyncBack replicates writes from the target back to the source after cutover. On Amazon DocumentDB, change streams must be enabled first:

```javascript
db.adminCommand({ modifyChangeStreams: 1, database: "<db>", collection: "", enable: true });
```

SyncBack hasn't been tested against Amazon DocumentDB in this fork.

## Tests

```bash
dotnet test OnlineMongoMigrationProcessor.Tests/OnlineMongoMigrationProcessor.Tests.csproj
```

## Keeping up with upstream

```bash
git fetch upstream
git merge upstream/main
```

## License

MIT, same as upstream. See [LICENSE.md](LICENSE.md).
