namespace CodeForge.Security;

/// <summary>Default exclusion rules: generated folders, secrets/environment files and binary content.</summary>
public sealed class ProjectFilePolicy
{
    private static readonly HashSet<string> GeneratedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", "bin", "obj", ".dart_tool", "node_modules", ".vs", ".idea",
        ".gradle", "TestResults", "__pycache__", ".pub-cache", "Pods"
    };

    private static readonly HashSet<string> SecretDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".aws", ".gnupg", ".azure", ".kube"
    };

    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".npmrc", ".pypirc", ".netrc", ".git-credentials", "secrets.json", "secret.json",
        "key.properties", "google-services.json", "GoogleService-Info.plist", "credentials", "credentials.json",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "service-account.json", "terraform.tfvars"
    };

    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".kdbx", ".ppk", ".secret", ".secrets", ".tfstate"
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".pdb", ".obj", ".o", ".so", ".dylib", ".lib", ".a", ".class", ".jar", ".apk", ".aab", ".ipa",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".psd", ".svgz",
        ".zip", ".7z", ".rar", ".gz", ".tar", ".nupkg", ".snupkg", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".mkv", ".ttf", ".otf", ".woff", ".woff2", ".eot",
        ".db", ".sqlite", ".mdb", ".bin", ".dat", ".pyc", ".cache"
    };

    private readonly bool _excludeBuildDir;

    /// <param name="excludeBuildDir">Flutter/Dart projects generate a top-level "build" folder.</param>
    public ProjectFilePolicy(bool excludeBuildDir = false) => _excludeBuildDir = excludeBuildDir;

    public bool IsExcludedDirectory(string name) =>
        GeneratedDirs.Contains(name) || SecretDirs.Contains(name) ||
        (_excludeBuildDir && name.Equals("build", StringComparison.OrdinalIgnoreCase));

    public bool IsSensitiveFileName(string fileName)
    {
        if (SecretNames.Contains(fileName)) return true;
        if (fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".example", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".sample", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".template", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase)) return true;
        return SecretExtensions.Contains(Path.GetExtension(fileName));
    }

    /// <summary>True if any directory segment of a root-relative path is excluded.</summary>
    public bool IsExcludedPath(string relativePath)
    {
        var parts = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
            if (IsExcludedDirectory(parts[i])) return true;
        return false;
    }

    public bool IsSensitivePath(string relativePath) =>
        IsSensitiveFileName(Path.GetFileName(relativePath));

    public static bool IsBinaryExtension(string extension) => BinaryExtensions.Contains(extension);

    public static bool LooksBinary(ReadOnlySpan<byte> head) => head.IndexOf((byte)0) >= 0;
}
