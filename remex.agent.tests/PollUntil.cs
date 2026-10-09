namespace Remex.Agent.Tests;

/// <summary>Waits for a condition rather than a duration, up to a generous ceiling.</summary>
internal static class PollUntil
{
    public static async Task TrueAsync(Func<bool> condition, string because, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, because);
            await Task.Delay(5);
        }
    }
}
