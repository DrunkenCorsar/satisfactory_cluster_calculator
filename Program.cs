using System.Diagnostics;
using SatisfactoryClusterCalculator;

SearchOptions options;
try
{
    options = SearchOptions.FromArgs(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return;
}

var outputPath = Path.GetFullPath(options.OutputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());

try
{
    if (!File.Exists(options.WorldJsonPath))
    {
        var message = $"""
            Missing world data file:
            {options.WorldJsonPath}

            Put a satisfactory-world-generator compatible default-world.json beside the executable/project,
            or pass a path explicitly:
              SatisfactoryClusterCalculator <world-json-path>
            """;

        File.WriteAllText(outputPath, message);
        OpenFileIfRequested(outputPath, options.OpenResult);
        return;
    }

    var nodes = WorldJsonLoader.LoadResourceNodes(options.WorldJsonPath);
    var searcher = new ClusteredSeedSearcher(nodes, outputPath);
    searcher.Search(options);

    OpenFileIfRequested(outputPath, options.OpenResult);
}
catch (Exception ex)
{
    File.WriteAllText(outputPath, ex.ToString());
    OpenFileIfRequested(outputPath, options.OpenResult);
}

static void OpenFileIfRequested(string path, bool openResult)
{
    if (openResult)
    {
        OpenFile(path);
    }
}

static void OpenFile(string path)
{
    Process.Start(new ProcessStartInfo
    {
        FileName = path,
        UseShellExecute = true,
    });
}
