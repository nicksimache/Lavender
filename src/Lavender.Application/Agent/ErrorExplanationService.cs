namespace Lavender.Application.Agent;

/// <summary>Predefined user-facing messages; raw diagnostics remain in the tool record.</summary>
public static class ErrorExplanationService
{
    public const string Fallback = "I couldn't complete this action.";

    public static string Explain(string action, string? diagnostic, string? outcome = null)
    {
        string detail = diagnostic ?? "";
        bool Has(string text) => detail.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (outcome == "cancelled") return "This action was cancelled before completion was confirmed.";
        if (outcome == "timed_out")
            return "I couldn't confirm whether this action finished before it timed out.";

        if (action.StartsWith("unity_", StringComparison.Ordinal))
        {
            if (Has("no Unity project") || Has("not a Unity project") || Has("Unity project not found") ||
                Has("Open the Unity project in Lavender first"))
                return "I couldn't identify a Unity project in this folder.";
            if (Has("does not match")) return "I couldn't match the open Unity project to this folder.";
            if (Has("disconnected") || Has("connect") || Has("busy"))
                return "I couldn't connect to the Unity editor for this project.";
            return "I couldn't complete this action in Unity.";
        }

        if (action is "lavender_get_git_status" or "lavender_get_git_diff")
        {
            if (Has("not a git repository") || Has("no git repository") || Has("repository not found"))
                return "I couldn't identify a Git repository in this folder.";
            return action == "lavender_get_git_status"
                ? "I couldn't check the Git repository's status." : "I couldn't read the changes in the Git repository.";
        }

        return action switch
        {
            "lavender_create_file" => "I couldn't create this file.",
            "lavender_write_lines" or "lavender_apply_edits" => "I couldn't update this file.",
            "lavender_delete_file" => "I couldn't delete this file.",
            "lavender_move_file" => "I couldn't move this file.",
            "lavender_read_file" or "lavender_read_source_file" => "I couldn't read this file.",
            "lavender_list_files" => "I couldn't list the files in this folder.",
            "lavender_list_assets" => "I couldn't list the image assets in this folder.",
            "lavender_inspect_image" => "I couldn't open this image.",
            "lavender_semantic_search" or "lavender_find_symbols" or "lavender_get_symbol_source" => "I couldn't inspect the requested code.",
            "lavender_get_diagnostics" => "I couldn't check this project's diagnostics.",
            _ => Fallback
        };
    }
}
