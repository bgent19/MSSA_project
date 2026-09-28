namespace MeepleLedger.Domain
{
    // A Game proposed in answer to an Ask, with its rank and the reason it was chosen.
    // A Recommendation without a reason is a guess, so Reason is validated here.
    // There is deliberately no similarity score: a score means different things in each
    // Provider, while rank is comparable across all of them.
    public class Recommendation
    {
        public Game Game { get; }

        // 1 is the best match.
        public int Rank { get; }

        public string Reason { get; }

        public Recommendation(Game game, int rank, string reason)
        {
            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }

            if (rank < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rank), "Rank starts at 1.");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A Recommendation must say why it was chosen.", nameof(reason));
            }

            Game = game;
            Rank = rank;
            Reason = reason.Trim();
        }
    }
}
