namespace MeepleLedger.Storage;

// Thrown by an IMeepleStore save or remove when the change did not reach the store. Pages catch this
// one type, so no page knows which store is live (ADR-0001). The original error is the inner exception.
public class StoreWriteException : Exception
{
    public StoreWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
