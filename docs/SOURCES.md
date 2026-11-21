# Sources and implementation lineage

The source corpus is downloaded from **dotnet/docs**, pinned by the revision and
per-file hashes in `data/sources.json`. Each indexed passage retains the exact
revision-specific GitHub URL. The application does not redistribute source
snapshots in Git. Documentation remains available under the upstream content
license, and code samples under their corresponding license:

- [dotnet/docs content license](https://github.com/dotnet/docs/blob/main/LICENSE)
- [dotnet/docs code license](https://github.com/dotnet/docs/blob/main/LICENSE-CODE)

The corpus comprises caching, HTTP resilience, IHttpClientFactory, factory
troubleshooting, HTTP rate limiting, HTTP logging, general logging, options,
options validation, dependency injection, worker services, and channels.
Unresolved Markdown includes and referenced snippets are omitted rather than
represented as extracted content.

## Existing companion patterns reused

The clients adapt the newsletter's established provider setup patterns:

- [Issue #46 companion](https://github.com/ElliotOne/nl-embedding-index-lifecycle-gates-rag-systems):
  `OpenAiCompatibleEmbeddingClient` informed the deployment, endpoint and explicit
  dimension settings. This project removes the local embedding path and uses
  Foundry only, batches input texts, and maps response indices explicitly.
- [Issue #50 companion](https://github.com/ElliotOne/nl-bounded-research-agent):
  `FoundryResearchModel` informed request-scoped `api-key` authentication and
  sanitized provider errors. This project also supports Azure Identity tokens
  and uses embeddings instead of chat/tool calls.

These are implementation lineage references, not verified publication revisions.
Historical metadata has no pinned commit for #46. The new companion has not been
committed or published by the authoring task.

## Provider references

The native query and index shapes follow Microsoft's official documentation:

- [Hybrid search queries](https://learn.microsoft.com/azure/search/hybrid-search-how-to-query)
- [RRF ranking](https://learn.microsoft.com/azure/search/hybrid-search-ranking)
- [Semantic ranking](https://learn.microsoft.com/azure/search/semantic-search-overview)
- [Create an index](https://learn.microsoft.com/rest/api/searchservice/indexes/create?view=rest-searchservice-2025-09-01)
- [Foundry Azure OpenAI v1 API](https://learn.microsoft.com/azure/ai-foundry/openai/api-version-lifecycle)
- [.NET 11](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)
