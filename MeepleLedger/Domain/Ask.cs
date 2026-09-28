namespace MeepleLedger.Domain
{
    // The natural-language sentence a person types when they want something to play,
    // e.g. "Something co-operative and quick for three." An Ask carries intent; a Table carries constraints.
    public class Ask
    {
        public string Text { get; }

        public Ask(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("An Ask cannot be empty.", nameof(text));
            }

            Text = text.Trim();
        }
    }
}
