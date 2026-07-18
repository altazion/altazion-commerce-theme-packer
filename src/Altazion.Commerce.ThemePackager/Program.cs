using System.Reflection;

namespace Altazion.Commerce.ThemePackager;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        if (IsVersion(args[0]))
        {
            Console.WriteLine(GetVersion());
            return 0;
        }

        if (!string.Equals(args[0], "pack", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintHelp();
            return 2;
        }

        try
        {
            var options = PackCommandOptions.Parse(args.Skip(1).ToArray());
            var result = ThemePackager.Pack(options);

            if (options.IsDryRun)
            {
                Console.WriteLine("Validation succeeded.");
                Console.WriteLine($"Source kind: {result.SourceKind}");
                Console.WriteLine($"Source: {result.ThemeName} ({result.ThemeId})");
                if (result.SourceKind == PackSourceKind.Template)
                    Console.WriteLine($"Profiles: {result.TemplateArtifacts.Count}");
                Console.WriteLine($"Files: {result.EntryCount} + manifests");
                Console.WriteLine("Dry run: no archive produced.");
                return 0;
            }

            if (result.SourceKind == PackSourceKind.Theme)
            {
                Console.WriteLine($"Pack created: {result.OutputPath}");
                Console.WriteLine($"Theme: {result.ThemeName} ({result.ThemeId})");
                Console.WriteLine($"Files: {result.EntryCount} + manifest");
                Console.WriteLine($"Size: {Math.Round(result.Size / 1024d, 1)} KB");
            }
            else
            {
                Console.WriteLine($"Template packs created: {result.OutputPath}");
                Console.WriteLine($"Template: {result.ThemeName} ({result.ThemeId})");
                Console.WriteLine($"Profiles: {result.TemplateArtifacts.Count}");
                foreach (var artifact in result.TemplateArtifacts)
                    Console.WriteLine($"- {artifact.ProfileCode}: {artifact.OutputPath}");
                Console.WriteLine($"Files: {result.EntryCount} + manifests");
                Console.WriteLine($"Size: {Math.Round(result.Size / 1024d, 1)} KB");
            }
            return 0;
        }
        catch (ThemePackagerException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static bool IsHelp(string arg)
        => arg is "-h" or "--help" or "help";

    private static bool IsVersion(string arg)
        => arg is "--version" or "version";

    private static string GetVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private static void PrintHelp()
    {
        Console.WriteLine("Altazion Commerce Theme Packager");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  altazion-theme-pack pack --source <path> [--output <file>] [--dry-run]");
        Console.WriteLine("  altazion-theme-pack --version");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -s, --source   Source folder containing either theme.general.json or template.json");
        Console.WriteLine("  -o, --output   Output file path for a theme source, or output directory for a template source");
        Console.WriteLine("      --dry-run  Validate the source without producing archives");
        Console.WriteLine("  -h, --help     Show help");
    }
}
