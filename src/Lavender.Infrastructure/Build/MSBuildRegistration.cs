using Microsoft.Build.Locator;

namespace Lavender.Infrastructure.Build;

internal static class MSBuildRegistration
{
    private static readonly object Gate = new();

    public static void EnsureRegistered()
    {
        if (MSBuildLocator.IsRegistered)
        {
            return;
        }

        lock (Gate)
        {
            if (MSBuildLocator.IsRegistered)
            {
                return;
            }

            try
            {
                MSBuildLocator.RegisterDefaults();
                return;
            }
            catch (InvalidOperationException) when (!MSBuildLocator.QueryVisualStudioInstances().Any())
            {
                string sdkPath = FindLatestDotNetSdkPath()
                    ?? throw new InvalidOperationException(
                        "No MSBuild instances were discovered and no .NET SDK MSBuild path could be found. Install the .NET SDK or set DOTNET_ROOT.");

                MSBuildLocator.RegisterMSBuildPath(sdkPath);
            }
        }
    }

    private static string? FindLatestDotNetSdkPath()
    {
        IEnumerable<string> roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet")
        }.Where(path => !string.IsNullOrWhiteSpace(path))!;

        return roots
            .Select(root => Path.Combine(root, "sdk"))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .Select(path => new { Path = path, Version = TryParseSdkVersion(Path.GetFileName(path)) })
            .Where(sdk => sdk.Version is not null)
            .OrderByDescending(sdk => sdk.Version)
            .Select(sdk => sdk.Path)
            .FirstOrDefault();
    }

    private static Version? TryParseSdkVersion(string? sdkDirectoryName)
    {
        if (string.IsNullOrWhiteSpace(sdkDirectoryName))
        {
            return null;
        }

        string stablePrefix = sdkDirectoryName.Split('-', 2)[0];
        return Version.TryParse(stablePrefix, out Version? version) ? version : null;
    }
}
