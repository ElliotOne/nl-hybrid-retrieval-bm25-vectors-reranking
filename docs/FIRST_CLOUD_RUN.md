# First cloud run

These are manual integration checks for the owner. They have not been run as
part of authoring this companion. No Azure resources were provisioned.

## Prepare the services

Use a dedicated Azure Search index on a Basic-or-higher service. Enable semantic
ranking and choose its free or standard billing plan intentionally. Deploy
`text-embedding-3-small` in Microsoft Foundry. Configure the resource roots and
deployment name in the environment variables documented in README.md.

Use Azure Identity with the documented Search and Foundry roles, or supply the
two service keys through your shell/secret store. Never put them in the source
manifest, query file, or report.

## Run the application

Run the following from the repo root with the .NET 11 SDK.

```powershell
dotnet restore HybridRetrieval.slnx --locked-mode
dotnet test HybridRetrieval.slnx -c Release --no-restore
dotnet run --project src/HybridRetrieval -c Release -- sources
dotnet run --project src/HybridRetrieval -c Release -- ingest
dotnet run --project src/HybridRetrieval -c Release -- compare 'AddStandardResilienceHandler'
dotnet run --project src/HybridRetrieval -c Release -- compare 'How can a producer wait when consumers cannot keep up?'
dotnet run --project src/HybridRetrieval -c Release -- evaluate 3
```

Check the real output before claiming that the integration works.

- The downloaded source hashes match `data/sources.json`.
- Ingestion prints a real chunk count and writes a receipt only after Search has
  indexed that number of chunks.
- All four modes return real source URLs and passages.
- Semantic mode returns reranker scores; a semantic error must fail the command.
- JSON reports show your index, embedding deployment, source revision, query
  version, complete ranked passages, and observed latency.
- Read the top passages for every query. Correct the seed relevance labels where
  another document provides a valid answer; keep this change explicit.
- Inspect API-name, paraphrase, and mixed queries separately. Do not select a
  strategy on aggregate recall alone.

## Troubleshoot a real failure

Provider failures print the operation, HTTP status, client request ID, Azure
request ID when supplied, and structured Azure error codes/messages (including
up to five error details). Semantic failures are labeled explicitly. Raw response
bodies, request payloads, and authentication headers are omitted. The active API
key or bearer token is redacted if Azure echoes it. Error messages are bounded
and flattened to avoid multiline terminal control characters. A non-JSON or
oversized error response produces an omission notice while retaining the status
and correlation IDs. Provider messages can still quote invalid field values;
review diagnostic text before sharing it if you use private inputs.

After changing diagnostics, rerun `compare` to reproduce the error. There is no
need to repeat ingestion. Common setup issues are:

| Symptom | Check |
| --- | --- |
| Authentication fails | Refresh `az login`, managed identity configuration, or the selected service key |
| Search 403 | Data-plane RBAC mode, role scope, admin-key availability, firewall/private network rules |
| Foundry 404 | Resource root and actual embedding deployment name |
| Foundry 400 | `text-embedding-3` model support and dimension count |
| Search semantic error | Semantic ranker enabled, region/tier availability, semantic quota/configuration |
| Existing index fingerprint differs | Choose a fresh index name for the new corpus/configuration |
| Index count does not settle | Check service indexing diagnostics and re-run idempotent ingestion |

## Record the evidence

Keep real JSON and Markdown reports in your private `artifacts/` directory.
Record SDK version, region, Search tier, semantic settings, model deployment,
dimensions and run time when sharing results. The service can update its managed
ranker, so a Git revision does not freeze the provider's ranking model.

After the cloud run, update the newsletter's walkthrough with the actual output
and create a verified companion Git revision before promoting the draft.
