namespace ion.compiler;

/// <summary>
/// The order the compiler reads a project's <c>*.ion</c> files in — and therefore the order of the declarations it
/// emits. It is the order .NET enumerates them on Windows, made explicit: NTFS lists a directory by name,
/// case-insensitively, and <see cref="SearchOption.AllDirectories"/> visits directories breadth-first (all files of
/// a level before the next level, sibling directories by name). Linux and macOS file systems list entries in
/// creation or hash order, so without this the same contracts generated a reordered client there — a "drift" for
/// any repository that commits its generated code and checks it on a Linux runner.
/// </summary>
public static class IonSourceOrder
{
    /// <summary>The files sorted as described, relative to <paramref name="root"/>.</summary>
    public static List<FileInfo> Sort(IEnumerable<FileInfo> files, DirectoryInfo root)
    {
        var rootPath = Path.TrimEndingDirectorySeparator(root.FullName);
        return files
            .Select(file => (file, segments: Segments(rootPath, file.FullName)))
            .OrderBy(x => x.segments, SegmentComparer.Instance)
            .Select(x => x.file)
            .ToList();
    }

    private static string[] Segments(string rootPath, string fullName) =>
        Path.GetRelativePath(rootPath, fullName)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Depth first (breadth-first visiting), then directory segments, then the file name — each by name.</summary>
    private sealed class SegmentComparer : IComparer<string[]>
    {
        public static readonly SegmentComparer Instance = new();

        public int Compare(string[]? x, string[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var byDepth = x.Length.CompareTo(y.Length);
            if (byDepth != 0) return byDepth;
            for (var i = 0; i < x.Length; i++)
            {
                var bySegment = StringComparer.OrdinalIgnoreCase.Compare(x[i], y[i]);
                if (bySegment != 0) return bySegment;
            }
            // Names that differ only by case (possible on Linux): ordinal, so the order is still total.
            for (var i = 0; i < x.Length; i++)
            {
                var exact = StringComparer.Ordinal.Compare(x[i], y[i]);
                if (exact != 0) return exact;
            }
            return 0;
        }
    }
}
