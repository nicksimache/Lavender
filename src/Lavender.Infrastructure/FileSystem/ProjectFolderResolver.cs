namespace Lavender.Infrastructure.FileSystem;

public static class ProjectFolderResolver
{
    public static string FindSolution(string directory)
    {
        string[] solutions = Directory.GetFiles(directory, "*.sln");
        if (solutions.Length == 1) return solutions[0];
        if (solutions.Length > 1) throw new InvalidOperationException("This folder has multiple solutions. Select the solution file to open.");
        string[] projects = Directory.GetFiles(directory, "*.csproj");
        if (projects.Length == 1) return projects[0];
        if (projects.Length > 1) throw new InvalidOperationException("This folder has multiple projects. Select the project file to open.");
        var children = Directory.EnumerateDirectories(directory)
            .Where(path => !ProjectScanner.ShouldIgnoreFolder(path) && new DirectoryInfo(path).LinkTarget is null).ToArray();
        solutions = children.SelectMany(path => Directory.GetFiles(path, "*.sln")).ToArray();
        if (solutions.Length == 1) return solutions[0];
        projects = children.SelectMany(path => Directory.GetFiles(path, "*.csproj")).ToArray();
        if (solutions.Length == 0 && projects.Length == 1) return projects[0];
        throw new InvalidOperationException("Select a .sln or .csproj file for this folder.");
    }
}
