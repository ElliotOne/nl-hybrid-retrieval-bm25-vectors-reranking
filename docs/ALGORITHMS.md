# From query terms to reranked passages

The implementation delegates indexing and retrieval to real Azure services.
This note explains what each stage contributes; it is not a second search engine.

## BM25

BM25 scores terms using inverse document frequency, saturating term frequency,
and document-length normalization. In one common form:

```text
score(q,d) = sum over query terms t of:
  IDF(t) * (tf(t,d) * (k1 + 1)) /
  (tf(t,d) + k1 * (1 - b + b * length(d) / averageLength))
```

This index explicitly selects BM25 with `k1=1.2`, `b=0.75` and the
`en.microsoft` text analyzer. Tokenization/stemming affect which code identifiers
survive as terms. BM25 is lexical relevance, not literal substring matching.
Use exact API-name queries to inspect the analyzer's practical effects.

## Dense retrieval

Foundry embeds document title plus chunk content and embeds the query once.
Azure Search's HNSW index retrieves vectors under cosine distance. Similar
meaning can surface despite different wording. HNSW is approximate; this project
does not substitute a local exhaustive cosine scan. Model/deployment/dimension
settings must match the indexed embeddings.

## Reciprocal rank fusion

The native hybrid request supplies both `search` and `vectorQueries`. Azure
Search combines the separate rankings using reciprocal rank fusion:

```text
RRF(d) = sum over result lists L containing d of 1 / (c + rank_L(d))
```

The fusion constant `c` is different from vector `k` and final display K. Fusion
uses rank positions so BM25 and vector score magnitudes need not be comparable.
The app uses native service defaults and does not expose a custom fusion constant.

## Semantic reranking

Semantic mode adds `queryType=semantic` and the configured title/content fields.
The managed ranker reads text for up to 50 initially selected candidates and
returns a separate `@search.rerankerScore`. It can reorder candidates, but cannot
rescue a passage absent from that candidate set. Increasing display K does not
expand the ranker's internal 50-result limit.

The score is a relevance signal, not a probability that the document is true.
A high score does not make out-of-date documentation correct. This project
measures relevance to a pinned corpus; freshness/access decisions are separate.

## Source-level metrics

All requests return the same chunk depth. The app keeps the first chunk from each
source and takes TopK source documents. Recall@K counts retrieved relevant sources
divided by all labeled relevant sources. Reciprocal rank is `1 / rank` for the
first relevant source, or zero. MRR averages that across query observations.

nDCG@K divides observed discounted gain by ideal discounted gain, using
`gain = 2^grade - 1` and `discount = log2(rank + 1)`. Unlisted sources have grade
zero. Deduplication prevents overlapping chunks from the same article receiving
multiple relevance credits. It can also hide poor passage selection within an
otherwise relevant article, so read the returned passages as well as the metrics.
