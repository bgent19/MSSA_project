# infra/production

The only part of `infra/` that production uses. Everything else in `infra/` is study material
(see [docs/study.md](../../docs/study.md)).

It will hold one idempotent script, `provision-production.ps1` (S-09), that creates the environment
[ADR-0004](../../docs/adr/0004-production-is-a-small-slice-of-the-study-build.md) describes, held to
about $5 a month:

- Resource group `rg-meepleledger-prod`, with a `CanNotDelete` lock
- Cosmos DB serverless (continuous backup, 7 days): the `games` container (vector and full-text
  policies) and the Owned Games and Plays containers
- Azure OpenAI with the embeddings deployment, for the Ask's vector
- Key Vault (RBAC only) with every secret the app reads
- Application Insights and its Log Analytics workspace, with a daily ingestion cap
- A Container Apps environment and the web app: min 0, max 1 replica, Single revision mode, image
  pulled from GitHub Container Registry
- Easy Auth (Microsoft sign-in, only the owner allowed), and an Entra app registration with a
  federated credential so GitHub Actions (S-10) can deploy to the app

Not deployed, on purpose: Service Bus, the worker, the function apps, Event Grid, Postgres, Redis,
App Configuration, App Service, AKS and ACR.

The script isn't written yet; this folder exists so S-09 has a place to put it.
