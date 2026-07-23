using Microsoft.CodeAnalysis;
using System.Xml.Linq;

namespace Lavender.Infrastructure.Indexing.Dependencies;

public enum ProjectDependencyNodeType
{
    Solution,
    Project,
    Package,
    Assembly
}

public enum ProjectDependencyType
{
    Contains,
    ReferencesProject,
    ReferencesPackage,
    ReferencesAssembly
}

public sealed class ProjectDependencyNode
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ProjectDependencyNodeType NodeType { get; init; }
    public string? FilePath { get; init; }
    public string? Version { get; init; }
    public string? TargetFramework { get; init; }
}

public sealed class ProjectDependencyEdge
{
    public string SourceNodeId { get; init; } = "";
    public string TargetNodeId { get; init; } = "";
    public ProjectDependencyType DependencyType { get; init; }
}

public sealed class ProjectDependencyGraph
{
    private readonly IReadOnlyDictionary<string, ProjectDependencyNode> _nodes;
    private readonly IReadOnlyList<ProjectDependencyEdge> _edges;

    public ProjectDependencyGraph(IEnumerable<ProjectDependencyNode> nodes, IEnumerable<ProjectDependencyEdge> edges)
    {
        _nodes = nodes.ToDictionary(x => x.Id);
        _edges = edges
            .DistinctBy(x => (x.SourceNodeId, x.TargetNodeId, x.DependencyType))
            .ToArray();
    }

    public IReadOnlyList<ProjectDependencyNode> GetProjects() => _nodes.Values.Where(x => x.NodeType == ProjectDependencyNodeType.Project).ToArray();
    public IReadOnlyList<ProjectDependencyEdge> GetDependencies(string id) => _edges.Where(x => x.SourceNodeId == id).ToArray();
    public IReadOnlyList<ProjectDependencyEdge> GetDependents(string id) => _edges.Where(x => x.TargetNodeId == id).ToArray();
    public IReadOnlyList<ProjectDependencyNode> GetProjectReferences(string id) => Targets(id, ProjectDependencyType.ReferencesProject);
    public IReadOnlyList<ProjectDependencyNode> GetPackages(string id) => Targets(id, ProjectDependencyType.ReferencesPackage);

    private IReadOnlyList<ProjectDependencyNode> Targets(string id, ProjectDependencyType type) =>
        _edges
            .Where(x => x.SourceNodeId == id && x.DependencyType == type)
            .Select(x => _nodes[x.TargetNodeId])
            .ToArray();
}

public sealed class ProjectDependencyIndexer
{
    public ProjectDependencyGraph Index(IndexedProjectContext context)
    {
        var nodes = new Dictionary<string, ProjectDependencyNode>();
        var edges = new List<ProjectDependencyEdge>();

        string solutionId = "solution:" + context.Solution.Id.Id;
        nodes[solutionId] = new()
        {
            Id = solutionId,
            Name = Path.GetFileNameWithoutExtension(context.InputPath),
            NodeType = ProjectDependencyNodeType.Solution,
            FilePath = context.InputPath
        };

        foreach (Project project in context.Solution.Projects)
        {
            string id = ProjectId(project.Id);
            string? framework = ReadTargetFramework(project.FilePath);

            nodes[id] = new()
            {
                Id = id,
                Name = project.Name,
                NodeType = ProjectDependencyNodeType.Project,
                FilePath = project.FilePath,
                TargetFramework = framework
            };

            edges.Add(new() { SourceNodeId = solutionId, TargetNodeId = id, DependencyType = ProjectDependencyType.Contains });

            foreach (ProjectReference reference in project.ProjectReferences)
            {
                edges.Add(new()
                {
                    SourceNodeId = id,
                    TargetNodeId = ProjectId(reference.ProjectId),
                    DependencyType = ProjectDependencyType.ReferencesProject
                });
            }

            foreach ((string name, string? version) in ReadPackages(project.FilePath))
            {
                string packageId = "package:" + name.ToLowerInvariant();
                nodes.TryAdd(packageId, new()
                {
                    Id = packageId,
                    Name = name,
                    Version = version,
                    NodeType = ProjectDependencyNodeType.Package
                });
                edges.Add(new()
                {
                    SourceNodeId = id,
                    TargetNodeId = packageId,
                    DependencyType = ProjectDependencyType.ReferencesPackage
                });
            }

            foreach (MetadataReference reference in project.MetadataReferences)
            {
                string name = reference.Display is null ? "unknown" : Path.GetFileNameWithoutExtension(reference.Display);
                string assemblyId = "assembly:" + name.ToLowerInvariant();
                nodes.TryAdd(assemblyId, new()
                {
                    Id = assemblyId,
                    Name = name,
                    FilePath = reference.Display,
                    NodeType = ProjectDependencyNodeType.Assembly
                });
                edges.Add(new()
                {
                    SourceNodeId = id,
                    TargetNodeId = assemblyId,
                    DependencyType = ProjectDependencyType.ReferencesAssembly
                });
            }
        }

        return new(nodes.Values, edges.Where(e => nodes.ContainsKey(e.TargetNodeId)));
    }

    public static string ProjectId(ProjectId id) => "project:" + id.Id;

    public static IReadOnlyList<(string Name, string? Version)> ReadPackages(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return Array.Empty<(string, string?)>();
        }

        try
        {
            XDocument xml = XDocument.Load(path);
            return xml
                .Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => (
                    (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"),
                    (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value))
                .Where(x => !string.IsNullOrWhiteSpace(x.Item1))
                .Select(x => (x.Item1!, x.Item2))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return Array.Empty<(string, string?)>();
        }
    }

    private static string? ReadTargetFramework(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            XDocument x = XDocument.Load(path);
            return x
                .Descendants()
                .FirstOrDefault(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                ?.Value;
        }
        catch
        {
            return null;
        }
    }
}
