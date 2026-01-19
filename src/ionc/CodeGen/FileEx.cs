namespace ion.compiler.CodeGen;

internal static class FileEx
{
    extension(DirectoryInfo directory)
    {
        public FileInfo File(string file) =>
            new(Path.Combine(directory.FullName, file));

        public DirectoryInfo Directory(string dir) =>
            new(Path.Combine(directory.FullName, dir));

        public DirectoryInfo Combine(string atFolder)
        {
            if (atFolder.StartsWith("@"))
                return new DirectoryInfo(Path.Combine(directory.FullName, atFolder.Replace("@", ".")));
            return new DirectoryInfo(atFolder);
        }
    }
}