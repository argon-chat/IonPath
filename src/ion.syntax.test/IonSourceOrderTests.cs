namespace ion.syntax.test;

using ion.compiler;

/// <summary>
/// <see cref="IonSourceOrder"/>: the compiler reads (and emits) a project's files in the order .NET enumerates them
/// on Windows, whatever the file system — Linux lists entries in creation or hash order, and the same contracts
/// generated a reordered TypeScript client there (a glue "drift" on a Linux CI runner).
/// </summary>
public class IonSourceOrderTests
{
    private static DirectoryInfo NewRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ion-order-" + Guid.NewGuid().ToString("N")));

    private static void Touch(DirectoryInfo root, string relative)
    {
        var path = Path.Combine(root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    private static List<string> Order(DirectoryInfo root, IEnumerable<FileInfo> files) =>
        IonSourceOrder.Sort(files, root)
            .Select(f => Path.GetRelativePath(root.FullName, f.FullName).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

    [Test]
    public void Files_of_a_flat_project_come_by_name_whatever_order_they_were_listed_in()
    {
        var root = NewRoot();
        try
        {
            // Created out of name order, as a Linux directory would list them.
            foreach (var name in new[] { "Shared.ion", "PosShifts.ion", "AgentLink.ion", "catalog.ion", "Orders.ion" })
                Touch(root, name);
            var listed = root.EnumerateFiles("*.ion", SearchOption.AllDirectories).Reverse();

            Assert.That(Order(root, listed),
                Is.EqualTo(new[] { "AgentLink.ion", "catalog.ion", "Orders.ion", "PosShifts.ion", "Shared.ion" }));
        }
        finally { root.Delete(true); }
    }

    [Test]
    public void Directories_are_visited_breadth_first_with_siblings_by_name_like_on_windows()
    {
        var root = NewRoot();
        try
        {
            foreach (var name in new[] { "b/z.ion", "a/y/deep.ion", "root.ion", "a/x.ion", "a-x/c.ion", "B2/q.ion" })
                Touch(root, name);

            Assert.That(Order(root, root.EnumerateFiles("*.ion", SearchOption.AllDirectories)),
                Is.EqualTo(new[] { "root.ion", "a/x.ion", "a-x/c.ion", "b/z.ion", "B2/q.ion", "a/y/deep.ion" }));
        }
        finally { root.Delete(true); }
    }

    [Test]
    public void The_order_is_total_for_names_that_differ_only_by_case()
    {
        var root = new DirectoryInfo(Path.GetTempPath());
        var upper = new FileInfo(Path.Combine(root.FullName, "A.ion"));
        var lower = new FileInfo(Path.Combine(root.FullName, "a.ion"));

        Assert.That(Order(root, [lower, upper]), Is.EqualTo(Order(root, [upper, lower])));
    }
}
