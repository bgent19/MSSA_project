namespace MeepleLedger.Domain
{
    // A Player is known only by name. Two names that differ only in case or surrounding spaces are
    // the same Player (CONTEXT.md), so every name comparison in the domain goes through here.
    public static class PlayerNames
    {
        public static bool AreSame(string first, string second)
        {
            return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // The spelling a Play stores: what was typed, without surrounding spaces.
        public static string Clean(string name)
        {
            return name.Trim();
        }
    }
}
