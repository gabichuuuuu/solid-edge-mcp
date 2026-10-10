namespace SolidEdgeMcp.Core;

/// <summary>Folders owned by the server under <c>%LocalAppData%\SolidEdgeMcp</c>.</summary>
public sealed class AppPaths
{
    public AppPaths(string dataRoot)
    {
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("The data root must be an absolute path.", nameof(dataRoot));
        }

        DataRoot = Path.GetFullPath(dataRoot);
        Work = Path.Combine(DataRoot, "work");
        Logs = Path.Combine(DataRoot, "logs");
        DefaultPolicyFile = Path.Combine(DataRoot, "policy.json");
    }

    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "SolidEdgeMcp"));

    public string DataRoot { get; }

    /// <summary>Work folder, always allowed (captures, temporary exports).</summary>
    public string Work { get; }

    public string Logs { get; }

    public string DefaultPolicyFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Logs);
    }
}
