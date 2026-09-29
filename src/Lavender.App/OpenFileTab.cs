using System.ComponentModel;
using System.IO;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace Lavender.App;

public sealed class OpenFileTab : INotifyPropertyChanged
{
    public string FullPath { get; }
    public string FileName => Path.GetFileName(FullPath);
    public TextDocument Document { get; }
    public Encoding Encoding { get; }
    private string _savedText;
    public bool IsDirty => Document.Text != _savedText;
    public bool IsSaved => !IsDirty;
    public string Header => FileName + (IsDirty ? " *" : "");
    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public OpenFileTab(string path)
    {
        FullPath = Path.GetFullPath(path);
        using var reader = new StreamReader(FullPath, new UTF8Encoding(false), true);
        _savedText = reader.ReadToEnd();
        Encoding = reader.CurrentEncoding;
        Document = new TextDocument(_savedText);
        Document.TextChanged += (_, _) => Notify();
    }

    public void MarkSaved() { _savedText = Document.Text; Notify(); }
    public void ReloadIfClean()
    {
        if (IsDirty || !File.Exists(FullPath)) return;
        string text = File.ReadAllText(FullPath);
        if (text == _savedText) return;
        _savedText = text;
        Document.Text = text;
        Notify();
    }
    private void Notify()
    {
        foreach (string name in new[] { nameof(IsDirty), nameof(IsSaved), nameof(Header) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
