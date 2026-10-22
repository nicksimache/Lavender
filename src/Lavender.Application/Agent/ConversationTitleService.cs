using System.Text.RegularExpressions;

namespace Lavender.Application.Agent;

/// <summary>A single bounded naming attempt for the first turn, independent of the main agent model.</summary>
public sealed class ConversationTitleService(Func<string, CancellationToken, Task<string>> generate)
{
    public async Task EnsureAsync(Conversation conversation, IConversationStore store, CancellationToken token)
    {
        if (conversation.Turns.Count != 1 || conversation.TitleGenerationAttempted ||
            !string.IsNullOrWhiteSpace(conversation.GeneratedTitle)) return;
        conversation.TitleGenerationAttempted = true;
        // Persist intent before sending: cancellation/reopening must not repeatedly charge for a title.
        await store.SaveAsync(conversation, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            string prompt = conversation.Turns[0].UserMessage;
            string title = await generate(prompt[..Math.Min(prompt.Length, 4000)], timeout.Token);
            title = Regex.Replace(title, @"\s+", " ").Trim().Trim('"', '\'', '`').Trim();
            if (!string.IsNullOrWhiteSpace(title))
                conversation.GeneratedTitle = title[..Math.Min(title.Length, 70)];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A naming outage or timeout must not prevent the coding request.
            System.Diagnostics.Trace.TraceWarning($"Chat naming failed: {ex.Message}");
        }
        await store.SaveAsync(conversation, token);
    }
}
