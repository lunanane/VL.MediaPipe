namespace MediaPipe;

/// <summary>
/// Where VL.MediaPipe's own files (web/, the MediaPipe page and models) are.
/// </summary>
/// <remarks>
/// The MediaPipe node serves web/ from AppHost.GetPackagePath("VL.MediaPipe"). That is null unless vvvv knows VL.MediaPipe
/// as a package (a package repository or a NuGet): a document that links VL.MediaPipe.vl as a file and is opened with a
/// double-click threw "Value cannot be null (Parameter 'path2')" in MakePath every frame, and the node showed pink with no
/// MediaPipe graph running. Then the package folder is found from the assembly itself: lib/net8.0/VL.MediaPipe.dll lives
/// two folders below the folder that holds VL.MediaPipe.vl and web/.
/// </remarks>
public static class PackageFolder
{
    /// <summary>
    /// <paramref name="packagePath"/> when vvvv resolved one (GetPackagePath), else the nearest folder above this assembly
    /// that holds VL.MediaPipe.vl and web/, else the assembly's own folder.
    /// </summary>
    public static string Resolve(string? packagePath)
    {
        if (!string.IsNullOrEmpty(packagePath)) return packagePath;
        return s_fromAssembly ??= FromAssembly();
    }

    private static string? s_fromAssembly;

    private static string FromAssembly()
    {
        var start = Path.GetDirectoryName(typeof(PackageFolder).Assembly.Location) ?? AppContext.BaseDirectory;
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VL.MediaPipe.vl")) && Directory.Exists(Path.Combine(dir.FullName, "web")))
                return dir.FullName;
        }
        return start;
    }
}
