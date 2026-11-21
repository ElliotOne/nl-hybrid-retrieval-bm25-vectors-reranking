# Hybrid Retrieval with BM25, Vectors, and Reranking

A .NET 11 retrieval application using **Microsoft Foundry embeddings** and
**Azure AI Search**. It downloads official .NET documentation, indexes real
embeddings, and compares four search strategies against the same content.
Companion to The Modern Engineer Issue #55, “Beyond Vector Search: Building
Better Search with BM25 and Reranking.” Primary area: Advanced AI Techniques.

## Overview

BM25 matches query terms. Dense vectors retrieve similar meaning. Hybrid search
combines their ranked lists through Azure Search's native reciprocal rank fusion
(RRF). Semantic ranking applies Microsoft's managed relevance models to the
fused candidates. This application lets you inspect the passages and measure
the tradeoffs instead of assuming each extra stage improves every query.

All runtime retrieval and embedding operations call real Azure services. There
is no local vector store, local model, deterministic embedding provider, mock
runtime, generated benchmark output, or fallback search engine. The .NET
application is a CLI host; the index and model run in Azure. Unit tests isolate
HTTP responses to check the client without spending service quota.

## What This Project Demonstrates

- BM25, dense HNSW search, native RRF hybrid search, and hybrid plus semantic ranking
- One Foundry query embedding reused across the three vector-based strategies
- A real corpus: 12 official dotnet/docs articles pinned to a Git revision and SHA-256 hashes
- Paragraph-aware overlapping chunks, batched Foundry embeddings, and Azure indexing
- Source-document deduplication so overlapping chunks do not fill the result list
- Source-level recall@K, reciprocal rank, nDCG@K, and observed client latency
- Inspectable per-query passages and actual provider scores in JSON reports
- Entra ID / managed identity authentication, with optional environment-only API keys

## Prerequisites

Install the [.NET 11 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/11.0).
The repo pins 11.0.100 and uses `.slnx`; the .NET 8 SDK cannot build it.

Provide these existing cloud resources:

1. An **Azure AI Search Basic or higher** service, with semantic ranking enabled.
   The free semantic pricing plan has a monthly request allowance; the standard
   semantic plan bills usage. Service capacity itself is also chargeable.
2. A **Microsoft Foundry Azure OpenAI resource** with a deployed
   `text-embedding-3-small` model. The configured deployment name can differ from
   the catalog model name. Default dimensions: 1536.
3. Network access from the host to those resources and to raw.githubusercontent.com.
   Private endpoints work when the host has the corresponding network and DNS access.

No chat model is needed: embeddings represent the text, and Azure Search's
managed semantic ranker scores candidates. A generative LLM would add another
variable to a retrieval lesson, so this project stops at ranked passages.

## Quick Start

Run all commands from this repository root. Set your endpoints first; the checked-in
configuration deliberately contains no real resource addresses or credentials.

```powershell
dotnet restore HybridRetrieval.slnx --locked-mode
dotnet test HybridRetrieval.slnx -c Release --no-restore

$env:HYBRID_Search__Endpoint = 'https://YOUR-SEARCH-SERVICE.search.windows.net'
$env:HYBRID_Search__IndexName = 'dotnet-hybrid-20261121'
$env:HYBRID_Foundry__Endpoint = 'https://YOUR-FOUNDRY-RESOURCE.openai.azure.com'
$env:HYBRID_Foundry__EmbeddingDeployment = 'YOUR-EMBEDDING-DEPLOYMENT'
$env:HYBRID_Foundry__Dimensions = '1536'

az login
dotnet run --project src/HybridRetrieval -c Release -- sources
dotnet run --project src/HybridRetrieval -c Release -- ingest
dotnet run --project src/HybridRetrieval -c Release -- compare 'AddStandardResilienceHandler'
dotnet run --project src/HybridRetrieval -c Release -- compare 'Stop calling an unhealthy downstream service for a while'
dotnet run --project src/HybridRetrieval -c Release -- evaluate 3
```

`sources` downloads and verifies the real documentation and checks query labels
without calling Azure. `ingest` creates the dedicated index, embeds document
chunks through Foundry, uploads them, and waits for the document count to settle.
It can be rerun after interruption: stable chunk IDs make uploads idempotent.
The same embedding requests are billed again on a rerun; no embedding cache is
implemented. Neither command deletes existing indexes.

Use a new index name when you change sources, chunking, deployment, resource,
dimensions, or semantic configuration. The existing index's description records
that configuration fingerprint and prevents an accidental mixed-space rebuild.
Do not edit this description or manually insert unrelated documents.

## Configuration

Settings load from the deployed `appsettings.json`, then an optional
`appsettings.Local.json` in your current working directory, then `HYBRID_`
environment variables. Double underscores select nested properties. The local
settings file is ignored by Git. `.env.example` documents variable names;
the program does **not** automatically load `.env` files.

| Variable | Purpose | Default |
| --- | --- | --- |
| `HYBRID_Search__Endpoint` | Azure Search resource root | Required |
| `HYBRID_Search__IndexName` | Dedicated index name | `dotnet-hybrid-20261121` |
| `HYBRID_Search__ApiVersion` | Stable Search REST API | `2025-09-01` |
| `HYBRID_Foundry__Endpoint` | Foundry Azure OpenAI resource root | Required |
| `HYBRID_Foundry__EmbeddingDeployment` | Your embedding deployment | `text-embedding-3-small` |
| `HYBRID_Foundry__Dimensions` | Embedding and index dimensions | `1536` |
| `HYBRID_Retrieval__TopK` | Unique source documents displayed/scored | `5` |
| `HYBRID_Retrieval__VectorCandidates` | Vector k and response chunk depth | `50` |
| `HYBRID_Ingestion__ChunkCharacters` | Maximum characters per chunk | `2200` |
| `HYBRID_Ingestion__OverlapCharacters` | Overlap between adjacent chunks | `250` |
| `HYBRID_Ingestion__EmbeddingBatchSize` | Texts per Foundry request | `8` |
| `HYBRID_ArtifactsDirectory` | Private report output path | `artifacts` |

The Foundry endpoint is the **resource root**, such as
`https://NAME.openai.azure.com` or `https://NAME.services.ai.azure.com`.
The client appends `/openai/v1/embeddings`. Do not supply a Foundry project URL
or append `/openai/v1` yourself. The model setting is your deployment name.
`dimensions` is sent explicitly, so use a `text-embedding-3` deployment that
supports the requested dimension count.

### Authentication

Use `DefaultAzureCredential` with Azure CLI for development or managed identity
on an Azure compute host. In this application, no tokens are written to disk or
reports. Environment credentials are supported through the standard Azure
Identity variables. Interactive browser authentication is not launched by the app.

Assign the host identity **Search Service Contributor** for index creation and
schema reads, **Search Index Data Contributor** for document upload/search,
and **Cognitive Services OpenAI User** on the Foundry resource. Enable role-based
data-plane access on the Search service. For a query-only deployment, remove
schema/ingestion operations and narrow permissions appropriately.

Alternatively set `HYBRID_SEARCH_API_KEY` to a Search **admin** key for ingestion
and `HYBRID_FOUNDRY_API_KEY` to your Foundry resource key. Each service chooses
its own key if present; otherwise it requests an Entra token for its scope.
Do not set both a stale key and expect the app to fall back to Entra ID.
The schema verification in `compare`/`evaluate` also requires schema read access,
so a Search query key by itself is insufficient for this CLI.

## How It Works

The source manifest pins actual documentation rather than authored pretend
incidents. Ingestion downloads each file at that revision, verifies its hash,
removes frontmatter, comments and unresolved include directives, and splits
the remaining Markdown near paragraph boundaries. It retains API names and
inline code. It does not expand external includes or execute code samples.

Each chunk stores source ID, original title, revision-specific source URL,
ordinal, content, and a Foundry vector. Title plus content is embedded;
title/content are also searchable text fields using `en.microsoft`. BM25 uses
`k1=1.2`, `b=0.75`. HNSW uses cosine distance. These are explicit starting
settings, not tuned optima for your corpus.

`compare` creates one real query embedding and makes four sequential Search
requests. Keyword requests contain `search`; vector-only requests contain
`vectorQueries`; hybrid requests contain both. Azure executes hybrid retrieval
and RRF internally. Semantic mode adds `queryType=semantic` and the index's
semantic configuration. It does not run a hand-written fusion approximation.

All modes request the same chunk response depth. Vector-bearing modes use at
least 50 nearest-neighbor candidates; semantic ranking considers up to 50
initial candidates internally. After Search orders the chunks, the application
keeps the first chunk per source, then displays/scores TopK distinct documents.
That source collapse happens **after** ranking and can yield fewer than K
sources when one source dominates the candidate pool.

## Reports and Measurement

Reports are created only by successful live commands. The repository contains
no precomputed cloud benchmark or claimed performance win. `evaluate 3` runs
12 queries × 4 modes × 3 repetitions: 144 Search requests, including 36 semantic
requests, plus 36 query-embedding calls. Ingestion embedding calls are extra.
Check your service quotas and pricing before increasing repetition counts.

The reports retain all returned chunks, including provider scores and passages.
Recall, MRR and nDCG use unique source documents. Seed labels assign relevance
grades 1–3 and are hand-authored for this corpus. They are neither production
traffic nor independently adjudicated ground truth. Unlisted documents count
as nonrelevant; inspect valid unlisted answers before interpreting a score loss.

Evaluation rotates request order, reports search p50/p95, and separately accounts
for the shared embedding observation. These are client wall-clock observations
including network, credential acquisition and retries. There is no explicit
warmup; initial cold requests remain in the results. The estimated standalone
latency is search time plus embedding time for vector modes. It is not a
concurrency/load test, and a short run cannot establish a reliable production p95.

BM25, RRF and semantic relevance scores are different scales. Do not compare
their raw numbers or interpret them as calibrated probabilities. Compare ranked
passages, labeled relevance and latency. Reports contain queries and source
text; treat them as private if you adapt the corpus to internal material.

## Project Structure

```text
HybridRetrieval.slnx
global.json
src/HybridRetrieval/
  Program.cs                 Configuration, DI, commands, cancellation, HTTP resilience
  Corpus.cs                  Real-source downloads, hashing, cleaning, chunking
  FoundryEmbeddings.cs       Foundry v1 embeddings client
  AzureSearch.cs             Index schema, uploads, four native query modes
  Evaluation.cs              Source-level ranking metrics
  Runner.cs                  Ingestion, comparisons, observed-result reports
data/
  sources.json               Pinned public documentation and hashes
  queries.json               Authored seed queries and graded source labels
tests/HybridRetrieval.Tests/  Unit tests; no Azure calls
scripts/refresh-sources.ps1   Explicitly update source revision/hashes
docs/                        Algorithms, first cloud run, and source attribution
Dockerfile                   Deployable .NET 11 CLI image
```

## Control Guarantees

The runtime has one cloud path per provider. Missing configuration, unavailable
credentials, rejected indexing records, dimension mismatch, and incomplete
semantic reranking stop the command. They never become simulated search results.
Transient HTTP failures use bounded retries and timeouts; provider error bodies
are not dumped, and authentication headers are not printed. Structured Azure
error codes/messages and correlation IDs are printed to diagnose service
failures, with bounded output and active credential redaction. An interrupted evaluation does not
write a completed report. This project does not deploy resources or make
promotion decisions.

## Tests

Run `dotnet test HybridRetrieval.slnx -c Release`. Tests cover request differences,
source-level ranking math, duplicate chunks, missing labels, chunk boundaries,
embedding response ordering, invalid vectors, cloud endpoint configuration,
sanitized HTTP failures, and semantic partial responses. These validate code
behavior, not Azure endpoint compatibility or retrieval quality.

See [the first cloud run checklist](docs/FIRST_CLOUD_RUN.md) for manual integration
testing. Cloud testing was deliberately left to the repository owner; no live
Foundry/Search benchmark is represented as verified.

## Container Deployment

Build the image with `docker build -t hybrid-retrieval:55 .`. It runs the same CLI
against Azure; there is no database/model container. Deploy as a manual Azure
Container Apps Job or another batch compute task with managed identity, resource
endpoints, and the roles described above. Pass `ingest`, `compare "..."`, or
`evaluate 3` as arguments. Mount persistent storage at `/app/artifacts` for reports.
The supplied Dockerfile runs as the non-root .NET application user. CI compiles,
tests, and builds the image without Azure credentials; it does not deploy it.

## License

The companion application is MIT licensed. Retrieved documentation and examples
retain their upstream licenses; see [source attribution](docs/SOURCES.md).
Downloading a source does not transfer its copyright to this project.

## Contributing

Keep improvements focused on retrieval: better analyzers, representative queries,
chunking, candidate selection, relevance labels, and measured performance. Add
tests for algorithmic behavior. Keep service credentials and private reports out
of Git. Record a real run before making claims about comparative quality.
