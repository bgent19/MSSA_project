# AI Project Enhancement Guideline

- Framework

   - MeepleLedger Blazor Web application
- Type of Enhancement

   - “What should we play tonight?” (the Tonight screen)
   - Given time playing, player count restraints, user types free response text about what they want to play.
   - Return is a list of recommended games, ranked, each with a generated reason explaining why

- Showcase integration of AI
   - Actual recommendation will be closest matches on a vector database (Vector embedding)
   - Reasoning text will be a chatbot response that knows the user input as well as the chosen game for the recommendation (Prompt engineering)

- Approach
   - Offline first: the feature works with an in-memory Search Index, then each Azure service is swapped in behind the same interface
   - Vector store implements one `IGameSearchIndex` interface (Ask + Table in, ranked Games out)

- Azure services
   - Azure OpenAI: embeddings for game Blurbs and the user's Ask
   - Cosmos DB (serverless): vector search over the games container
   - Planned: PostgreSQL with pgvector, Azure Managed Redis (cache and vector index), and generated reasons

- Progress
   - Done: domain types (Table, Ask, Recommendation), Search Index seam, Tonight screen, embeddings, Cosmos Search Index
   - Provisioning scripts live in `infra/`; billable resources are torn down when work pauses
