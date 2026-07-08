namespace Altazion.Commerce.ThemePackager;

internal sealed class PackCommandOptions
{
    public required string SourceDirectory { get; init; }

    public string? OutputFile { get; init; }

    public static PackCommandOptions Parse(string[] args)
    {
        string? sourceDirectory = null;
        string? outputFile = null;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            switch (arg)
            {
                case "-s":
                case "--source":
                    sourceDirectory = ReadValue(args, ref index, arg);
                    break;

                case "-o":
                case "--output":
                    outputFile = ReadValue(args, ref index, arg);
                    break;

                default:
                    throw new ThemePackagerException($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ThemePackagerException("Missing required option '--source'.");

        return new PackCommandOptions
        {
            SourceDirectory = sourceDirectory,
            OutputFile = outputFile,
        };
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ThemePackagerException($"Missing value for option '{option}'.");

        index++;
        return args[index];
    }
}