using Lavender.Application.Agent;
using Lavender.Infrastructure.AI;

string directory = Path.Combine(Path.GetTempPath(), "Lavender titles " + Guid.NewGuid());
try
{
    var store = new JsonConversationStore(directory);
    var chat = await store.CreateAsync();
    int calls = 0;
    var titles = new ConversationTitleService((prompt, token) =>
    {
        calls++;
        Check(prompt.Length == 4000, "Bounded first-prompt input");
        Check(chat.TitleGenerationAttempted, "Attempt recorded before API request");
        return Task.FromResult("\"Unity\nPlayer Movement\"");
    });
    await titles.EnsureAsync(chat, store, default);
    Check(calls == 0, "Empty chat makes no request");
    chat.Turns.Add(Turn(new string('x', 5000)));
    bool notified = false;
    chat.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(chat.Title);
    await titles.EnsureAsync(chat, store, default);
    Check(chat.Title == "Unity Player Movement" && notified, "Generated title is normalized and notifies UI");
    var reopenedStore = new JsonConversationStore(directory);
    var reopened = (await reopenedStore.LoadAsync(chat.Id))!;
    await titles.EnsureAsync(reopened, reopenedStore, default);
    reopened.Turns.Add(Turn("Another question"));
    await titles.EnsureAsync(reopened, reopenedStore, default);
    Check(calls == 1 && reopened.Title == chat.Title, "Title persists without another call on reopen or follow-up");
    var failure = await store.CreateAsync();
    failure.Turns.Add(Turn("Fix player jumping"));
    int failures = 0;
    var unavailable = new ConversationTitleService((_, _) => { failures++; throw new InvalidOperationException("offline"); });
    await unavailable.EnsureAsync(failure, store, default);
    await unavailable.EnsureAsync(failure, store, default);
    Check(failures == 1 && failure.Title == "Fix player jumping", "Naming failure preserves fallback without repeated calls");
    var old = await store.CreateAsync();
    old.Turns.Add(Turn("Previous prompt")); old.Turns.Add(Turn("New prompt"));
    await unavailable.EnsureAsync(old, store, default);
    Check(failures == 1, "Existing multi-turn chats are not renamed");
    var cancelled = await store.CreateAsync();
    cancelled.Turns.Add(Turn("Cancelled request"));
    using var cancellation = new CancellationTokenSource();
    var interrupted = new ConversationTitleService((_, _) =>
    {
        cancellation.Cancel();
        throw new OperationCanceledException(cancellation.Token);
    });
    try { await interrupted.EnsureAsync(cancelled, store, cancellation.Token); throw new Exception("Cancellation was swallowed"); }
    catch (OperationCanceledException) { }
    Check((await new JsonConversationStore(directory).LoadAsync(cancelled.Id))!.TitleGenerationAttempted,
        "Cancelled attempt remains recorded on disk");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }

static ConversationTurn Turn(string prompt) => new() { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, UserMessage = prompt };
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
