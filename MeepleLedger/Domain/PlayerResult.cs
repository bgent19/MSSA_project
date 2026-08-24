namespace MeepleLedger.Domain
{
    public class PlayerResult
    {
        public required string PlayerName { get; set; }
        public int? Score { get; set; }
        public bool IsWinner { get; set; }
    }
}
