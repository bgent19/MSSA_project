---
status: accepted
---

# Production is a small slice of the study build, held to about $5 a month

The AI-200 track built far more Azure than a one-owner app needs: Service Bus, a worker, function
apps, Event Grid, Redis, Postgres, App Configuration, App Service, AKS and ACR, each to learn a
service. The shipped app runs only what it uses, under a standing ceiling of about $5 a month:
Container Apps (scale to zero, at most one replica), Cosmos DB serverless for the Search Index and
the durable Collection and Play Log, Azure OpenAI for Ask embeddings, Key Vault, and Application
Insights. Everything else stays in the repo, off by default, as study material that is never
deployed. A cold start on the first request of the evening is the accepted price.

## Considered Options

- **ACR for the production image.** Rejected: Basic costs about $5 a month just for existing, the
  whole ceiling. The image lives in GitHub Container Registry as a private package, built by GitHub
  Actions; Container Apps pulls it with a token held as a registry secret. This gives up A-24's
  identity-only pull, and the token expires and must be rotated. The ACR scripts stay for study.
- **Embedding pipeline in production (Service Bus, worker, change feed, functions).** Rejected: the
  Catalog changes only when the owner runs the Seeder, so `embed` and `upload-cosmos` from a
  workstation cover it, and a Standard Service Bus namespace alone is over the ceiling.
- **Postgres or Redis as the live Provider.** Rejected on cost (each is over the ceiling running
  24/7). Both remain switchable Providers per ADR-0001 and ADR-0002.
