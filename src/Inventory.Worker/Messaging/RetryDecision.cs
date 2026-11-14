using System.Text;

namespace Inventory.Worker.Messaging;

public enum RetryAction
{
    Retry,
    DeadLetter
}

public static class RetryDecision
{
    public static RetryAction For(int attempt, int maxAttempts)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "maxAttempts must be at least 1.");
        }

        return attempt < maxAttempts ? RetryAction.Retry : RetryAction.DeadLetter;
    }

    public static int ReadAttempt(IDictionary<string, object>? headers, string attemptHeader)
    {
        if (headers is null || !headers.TryGetValue(attemptHeader, out var raw))
        {
            return 1;
        }

        var parsed = raw switch
        {
            int i => i,
            long l => (int)l,
            byte b => b,
            sbyte sb => sb,
            short s => s,
            byte[] bytes when int.TryParse(Encoding.UTF8.GetString(bytes), out var value) => value,
            string str when int.TryParse(str, out var value) => value,
            _ => 1
        };

        return parsed < 1 ? 1 : parsed;
    }
}
