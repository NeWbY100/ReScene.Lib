namespace ReScene.Tests;

/// <summary>
/// Shared accessor for the static test-data directory copied next to the test
/// assembly (see the <c>TestData\**\*</c> item in the .csproj). Replaces the
/// per-class <c>TestDataPath</c>/<c>TestDataDir</c> constants and the bespoke
/// <c>TestFile</c>/<c>Path_</c> combine helpers.
/// </summary>
internal static class TestData
{
    /// <summary>Absolute path to the test-data root next to the test assembly.</summary>
    public static readonly string Root = System.IO.Path.Combine(AppContext.BaseDirectory, "TestData");

    /// <summary>Combines <see cref="Root"/> with the supplied relative path parts.</summary>
    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([Root, .. parts]);
}
