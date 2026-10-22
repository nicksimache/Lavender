using Lavender.Infrastructure.FileSystem;

string root = Path.Combine(Path.GetTempPath(), "Lavender watcher " + Guid.NewGuid());
Directory.CreateDirectory(Path.Combine(root, "Assets"));
Directory.CreateDirectory(Path.Combine(root, "Library"));
using var changed = new AutoResetEvent(false);
using var watcher = new ProjectFileWatcher(root, () => changed.Set());
try
{
    string file = Path.Combine(root, "Assets", "Player.cs");
    ExpectChange(() => File.WriteAllText(file, "class Player {}"), "External file creation");
    ExpectChange(() => File.AppendAllText(file, "\n// update"), "External file modification");
    string renamed = Path.Combine(root, "Assets", "Hero.cs");
    ExpectChange(() => File.Move(file, renamed), "External rename");
    ExpectChange(() => File.Delete(renamed), "External deletion");
    ExpectChange(() => Directory.CreateDirectory(Path.Combine(root, "Assets", "NewFolder")), "External folder creation");
    Drain();
    File.WriteAllText(Path.Combine(root, "Library", "generated.cs"), "ignored");
    File.WriteAllText(Path.Combine(root, "Assets", "Player.cs.meta"), "ignored");
    if (changed.WaitOne(500)) throw new Exception("Generated folders or metadata caused refresh.");
    Console.WriteLine("PASS: Unity generated files and metadata are ignored");
    watcher.Dispose();
    File.WriteAllText(file, "after disposal");
    if (changed.WaitOne(250)) throw new Exception("Disposed watcher delivered an event.");
    Console.WriteLine("PASS: Disposal stops notifications");
}
finally
{
    watcher.Dispose();
    Directory.Delete(root, recursive: true);
}

void Drain()
{
    while (changed.WaitOne(100)) { }
}
void ExpectChange(Action action, string label)
{
    Drain();
    action();
    if (!changed.WaitOne(5000)) throw new Exception(label + " was not detected.");
    Console.WriteLine("PASS: " + label);
}
