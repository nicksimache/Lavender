using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lavender.src.Lavender.Infrastructure.FileSystem
{
    public class SolutionLoader
    {
        public static async Task<Project> LoadProjectAsync(
            string projectPath,
            CancellationToken cancellationToken = default)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            using var workspace = MSBuildWorkspace.Create();


            using var registration =
                workspace.RegisterWorkspaceFailedHandler(args =>
                {
                    Console.WriteLine(
                        $"Roslyn workspace warning: {args.Diagnostic.Message}");
                });

            return await workspace.OpenProjectAsync(
                projectPath,
                cancellationToken: cancellationToken);
        }

        public static async Task<Solution> LoadSolutionAsync(
            string solutionPath,
            CancellationToken cancellationToken = default)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            using var workspace = MSBuildWorkspace.Create();

            using var registration =
                workspace.RegisterWorkspaceFailedHandler(args =>
                {
                    Console.WriteLine(
                        $"Roslyn workspace warning: {args.Diagnostic.Message}");
                });

            return await workspace.OpenSolutionAsync(
                solutionPath,
                cancellationToken: cancellationToken);
        }
    }
}
