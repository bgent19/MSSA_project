using MeepleLedger.Domain;

namespace MeepleLedger.Storage;

// The Search Index: turns an Ask plus a Table into ranked Recommendations.
// Every Provider (in-memory, Cosmos DB, PostgreSQL, Redis) implements this one interface,
// so nothing here may be specific to vectors, SQL, request units or keys (ADR-0002).
//
// The contract every implementation must honour:
//   1. The Table is applied BEFORE ranking. Only Games the Table admits are ranked, and when
//      Table.OwnedOnly is true only Games in the Collection are ranked.
//   2. At most maxResults Recommendations come back. Fewer come back only when fewer Games
//      pass the Table, never because results were trimmed after ranking.
//   3. Ranks start at 1 and have no gaps.
//   4. When two Games match the Ask equally well, the one whose Name sorts first
//      (ordinal, ignoring case) gets the better rank.
//   5. Every Recommendation carries a Reason a person would accept.
public interface IGameSearchIndex
{
    Task<IReadOnlyList<Recommendation>> RecommendAsync(Ask ask, Table table, int maxResults);
}
