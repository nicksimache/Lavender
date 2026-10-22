using System.Windows.Input;
using Lavender.Infrastructure.Editing;

namespace Lavender.App;

public partial class MainWindow
{
    private async void ProblemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProblemsList.SelectedItem is not EditorDiagnostic diagnostic) return;
        try { await OpenCodeFileAtLineAsync(diagnostic.FilePath, diagnostic.Line, diagnostic.Column); }
        catch (Exception ex) { AppendAgentActivity("Cannot open diagnostic: " + ex.Message); }
    }
}
