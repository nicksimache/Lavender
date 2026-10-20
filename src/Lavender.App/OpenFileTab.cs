using System.ComponentModel;
using System.IO;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace Lavender.App;

/// <summary>Owns a tab's editable document, last saved text, and caret/scroll position.</summary>
public sealed class OpenFileTab : INotifyPropertyChanged
{
    private string _savedText;

    public string FullPath { get; }
    public string FileName => Path.GetFileName(FullPath);
    public TextDocument Document { get; }
    public Encoding Encoding { get; }
    public bool IsImage => Rendering.ImageAssets.IsImage(FullPath);

    public bool IsDirty => !IsImage && Document.Text != _savedText;
    public bool IsSaved => !IsDirty;
    public string Header => FileName + (IsDirty ? " *" : "");

    public int CaretOffset { get; set; }
    public double VerticalOffset { get; set; }
    public double HorizontalOffset { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Loads the file into an editable document and tracks its saved text and encoding.</summary>
    public OpenFileTab(string path)
    {
        FullPath = Path.GetFullPath(path);
        if (IsImage)
        {
            _savedText = "";
            Encoding = new UTF8Encoding(false);
            Document = new TextDocument();
            return;
        }

        using var reader = new StreamReader(FullPath, new UTF8Encoding(false), true);
        _savedText = reader.ReadToEnd();
        Encoding = reader.CurrentEncoding;

        Document = new TextDocument(_savedText);
        Document.TextChanged += (_, _) => Notify();
    }

    /// <summary>Records the current document as saved and updates the tab's displayed state.</summary>
    public void MarkSaved()
    {
        _savedText = Document.Text;
        Notify();
    }

    /// <summary>Reloads external file changes only when the tab has no unsaved edits.</summary>
    public void ReloadIfClean()
    {
        // Agent edits can refresh saved tabs, but must never overwrite unsaved editor changes.
        if(IsImage || IsDirty || !File.Exists(FullPath))
        {
            return;
        }

        string text = File.ReadAllText(FullPath);

        if(text == _savedText)
        {
            return;
        }

        _savedText = text;
        Document.Text = text;
        Notify();
    }

    /// <summary>Notifies bindings that the saved state and tab header may have changed.</summary>
    private void Notify()
    {
        foreach (string name in new[] { nameof(IsDirty), nameof(IsSaved), nameof(Header) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
