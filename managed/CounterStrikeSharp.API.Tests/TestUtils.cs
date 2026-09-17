using System.Reflection;

namespace CounterStrikeSharp.API.Tests;

public static class TestUtils
{
    public static string GetTestPath(string relativePath)
    {
        var codeBaseUrl = new Uri(Assembly.GetExecutingAssembly().Location);
        var codeBasePath = Uri.UnescapeDataString(codeBaseUrl.AbsolutePath);
        var dirPath = Path.GetDirectoryName(codeBasePath)
                      ?? throw new InvalidOperationException($"Test assembly path '{codeBasePath}' has no directory");
        return Path.Combine(dirPath, "Resources", relativePath);
    }
}
