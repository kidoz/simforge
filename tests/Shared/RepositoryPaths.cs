namespace SimForge.Tests.Shared;

internal static class RepositoryPaths
{
    /// <summary>Walks up from the test output directory to the directory containing SimForge.slnx.</summary>
    public static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SimForge.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the repository root (SimForge.slnx) above " + AppContext.BaseDirectory);
        }
    }
}
