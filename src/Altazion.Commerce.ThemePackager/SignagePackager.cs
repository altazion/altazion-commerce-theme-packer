using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Altazion.Commerce.ThemePackager;

internal sealed record SignagePackResult(string OutputPath, int FileCount, long Size, string Code, string Name);

internal static class SignagePackager
{
    private const long MaxArchive = 100_000_000;
    private const long MaxContent = 250_000_000;
    private const int MaxFiles = 5000;
    private const int MaxManifest = 1024 * 1024;
    private static readonly Regex Code = new(@"\A[A-Za-z0-9][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex Version = new(@"\A[A-Za-z0-9][A-Za-z0-9_.+-]*\z", RegexOptions.CultureInvariant);
    private static readonly Regex Color = new(@"\A#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> OptionTypes = new(StringComparer.Ordinal)
    {
        "text", "multilineText", "number", "boolean", "color", "choice", "image",
        "media", "url", "segmentation", "productSelection", "html"
    };

    public static SignagePackResult Pack(PackCommandOptions options)
    {
        var source = ThemePackager.ResolveSourceDirectory(options.SourceDirectory);
        RejectLinksInExistingPath(source);
        var sourceName = Path.TrimEndingDirectorySeparator(source);
        var parent = Path.GetDirectoryName(sourceName);
        if (options.OutputFile == null && parent == null)
            throw Error("An explicit output is required for a filesystem root source.");
        var output = Path.GetFullPath(options.OutputFile ?? Path.Combine(parent!, Path.GetFileName(sourceName) + ".altzsignage"));
        var relativeOutput = Path.GetRelativePath(source, output);
        if (relativeOutput == "." || (!Path.IsPathRooted(relativeOutput)
            && relativeOutput != ".." && !relativeOutput.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw Error("The output must be outside the source directory.");
        RejectLinksInExistingPath(output);

        var files = new List<(string Path, string Name)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = 0;
        Collect(source, "", files, paths, ref directories);
        var manifest = files.SingleOrDefault(f => f.Name == "manifest.json");
        if (manifest.Path == null)
            throw Error("The source must contain manifest.json at its root with exact casing.");
        RejectLinksInExistingPath(manifest.Path);
        var manifestBytes = ReadManifest(manifest.Path);
        var (code, name) = ValidateManifest(manifestBytes, files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal));

        using var buffer = new MemoryStream();
        using (var bounded = new BoundedArchiveStream(buffer))
        using (var zip = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true))
        {
            long total = 0;
            var chunk = new byte[81920];
            foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                RejectLinksInExistingPath(file.Path);
                using Stream input = file.Name == "manifest.json"
                    ? new MemoryStream(manifestBytes, writable: false)
                    : File.OpenRead(file.Path);
                var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                int count;
                while ((count = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (count > MaxContent - total)
                        throw Error("The uncompressed package exceeds 250000000 bytes.");
                    total += count;
                    entryStream.Write(chunk, 0, count);
                }
            }
        }

        if (!options.IsDryRun)
        {
            var outputDirectory = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(outputDirectory);
            RejectLinksInExistingPath(output);
            var staging = Path.Combine(outputDirectory, "." + Guid.NewGuid().ToString("N") + ".signage-staging");
            var created = false;
            try
            {
                using (var destination = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    buffer.Position = 0;
                    buffer.CopyTo(destination);
                }
                File.Move(staging, output, overwrite: true);
                created = false;
            }
            finally
            {
                if (created)
                    File.Delete(staging);
            }
        }
        return new SignagePackResult(options.IsDryRun ? "" : output, files.Count, buffer.Length, code, name);
    }

    internal static bool HasSignageSchema(string path)
    {
        RejectLinksInExistingPath(path);
        if (new FileInfo(path).Length > MaxManifest)
            return false;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(ReadManifest(path));
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any(property =>
                    property.NameEquals("schemaVersion")
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() == "signage-1");
        }
        catch (JsonException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void Collect(string root, string prefix, List<(string Path, string Name)> files,
        HashSet<string> paths, ref int directories)
    {
        var directory = prefix.Length == 0 ? root : Path.Combine(root, prefix.Replace('/', Path.DirectorySeparatorChar));
        RejectLink(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(path);
            var name = prefix.Length == 0 ? Path.GetFileName(path) : prefix + "/" + Path.GetFileName(path);
            ValidatePath(name);
            if (!paths.Add(name))
                throw Error("Duplicate or conflicting path: " + name);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            {
                if (++directories > MaxFiles * 4)
                    throw Error("The source contains too many directories.");
                Collect(root, name, files, paths, ref directories);
            }
            else
            {
                if (files.Count >= MaxFiles)
                    throw Error("The source contains more than 5000 files.");
                files.Add((path, name));
            }
        }
    }

    private static void ValidatePath(string path)
    {
        if (path.Length > 500 || !path.IsNormalized(NormalizationForm.FormC)
            || path.Any(c => char.IsControl(c) || c is '\\' or ':' or '?' or '#' or '%' or '\uFFFD')
            || path.Split('/').Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith(' ')))
            throw Error("Invalid package path: " + path);
    }

    private static void RejectLinksInExistingPath(string path)
    {
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                RejectLink(current);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Error("Symbolic links and reparse points are not supported: " + path);
    }

    private static byte[] ReadManifest(string path)
    {
        using var input = File.OpenRead(path);
        using var output = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (output.Length + count > MaxManifest)
                throw Error("manifest.json exceeds 1 MiB.");
            output.Write(chunk, 0, count);
        }
        return output.ToArray();
    }

    private static (string Code, string Name) ValidateManifest(byte[] bytes, HashSet<string> files)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            CheckDuplicates(document.RootElement);
            var root = document.RootElement;
            Object(root, "manifest", "schemaVersion", "template", "entryPoint", "optionGroups", "animationZones");
            if (String(root, "schemaVersion", 100) != "signage-1")
                throw Error("schemaVersion must be signage-1.");
            var template = Property(root, "template", JsonValueKind.Object);
            Object(template, "template", "code", "name", "version");
            var code = String(template, "code", 100);
            CheckCode(code, "template code");
            var name = String(template, "name", 250);
            if (!Version.IsMatch(String(template, "version", 100)))
                throw Error("Invalid template version.");
            var entry = String(root, "entryPoint", 500);
            if (!entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || !files.Contains(entry))
                throw Error("entryPoint must exactly match a packaged HTML file.");

            var groupCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var optionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in Property(root, "optionGroups", JsonValueKind.Array).EnumerateArray())
            {
                Object(group, "option group", "code", "label", "options");
                UniqueCode(group, "code", groupCodes);
                String(group, "label", 250);
                foreach (var option in Property(group, "options", JsonValueKind.Array).EnumerateArray())
                    ValidateOptionStructure(option, optionKeys);
            }
            var zoneCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var zone in Property(root, "animationZones", JsonValueKind.Array).EnumerateArray())
            {
                Object(zone, "animation zone", "code", "label", "renderMode", "contentTypes");
                UniqueCode(zone, "code", zoneCodes);
                String(zone, "label", 250);
                if (String(zone, "renderMode", 10) is not ("html" or "data"))
                    throw Error("renderMode must be html or data.");
                if (zone.TryGetProperty("contentTypes", out var types))
                {
                    Kind(types, JsonValueKind.Array, "contentTypes");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var type in types.EnumerateArray())
                    {
                        var value = StringValue(type, "contentTypes", 100);
                        CheckCode(value, "contentTypes");
                        if (!seen.Add(value)) throw Error("Duplicate content type: " + value);
                    }
                }
            }
            return (code, name);
        }
        catch (DecoderFallbackException)
        {
            throw Error("manifest.json must be valid UTF-8.");
        }
        catch (JsonException ex)
        {
            throw Error("Invalid manifest JSON: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw Error("Invalid manifest JSON string: " + ex.Message);
        }
    }

    private static void ValidateOptionStructure(JsonElement option, HashSet<string> keys)
    {
        Object(option, "option", "key", "label", "type", "required", "description", "defaultValue", "min", "max", "choices");
        UniqueCode(option, "key", keys);
        String(option, "label", 250);
        var type = String(option, "type", 50);
        if (!OptionTypes.Contains(type)) throw Error("Unsupported option type: " + type);
        if (option.TryGetProperty("required", out var required) && required.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error("required must be a boolean.");
        if (option.TryGetProperty("description", out var description))
        {
            Kind(description, JsonValueKind.String, "description");
            if (description.GetString()!.Length > 4000) throw Error("description exceeds 4000 characters.");
        }
        decimal? min = null, max = null;
        if (option.TryGetProperty("min", out var minElement))
            min = type == "number" ? Number(minElement, "min") : throw Error("min is only permitted on number options.");
        if (option.TryGetProperty("max", out var maxElement))
            max = type == "number" ? Number(maxElement, "max") : throw Error("max is only permitted on number options.");
        if (min > max) throw Error("min must not exceed max.");
        var choicesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var choiceValues = new HashSet<string>(StringComparer.Ordinal);
        if (option.TryGetProperty("choices", out var choices))
        {
            if (type != "choice") throw Error("choices is only permitted on choice options.");
            Kind(choices, JsonValueKind.Array, "choices");
            foreach (var choice in choices.EnumerateArray())
            {
                Object(choice, "choice", "value", "label");
                var value = String(choice, "value", 250);
                if (!choicesSeen.Add(value)) throw Error("Duplicate choice: " + value);
                choiceValues.Add(value);
                String(choice, "label", 250);
            }
            if (choicesSeen.Count == 0) throw Error("choice options require nonempty choices.");
        }
        else if (type == "choice") throw Error("choice options require choices.");

        if (option.TryGetProperty("defaultValue", out var defaultValue))
            ValidateDefault(type, required.ValueKind == JsonValueKind.True, defaultValue, min, max, choiceValues);
    }

    private static void ValidateDefault(string type, bool required, JsonElement value, decimal? min, decimal? max, HashSet<string> choices)
    {
        switch (type)
        {
            case "text":
            case "multilineText":
            case "html":
                StringValue(value, "defaultValue", MaxManifest, required);
                break;
            case "number":
                var number = Number(value, "defaultValue");
                if (number < min || number > max) throw Error("The number defaultValue is outside its bounds.");
                break;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Error("The boolean defaultValue must be a boolean.");
                break;
            case "color":
                if (!Color.IsMatch(StringValue(value, "defaultValue", 9)))
                    throw Error("The color defaultValue must be a hex color.");
                break;
            case "choice":
                if (!choices.Contains(StringValue(value, "defaultValue", 250)))
                    throw Error("The choice defaultValue is not declared.");
                break;
            case "url":
                SafeUrl(value, "defaultValue");
                break;
            case "image":
            case "media":
                Object(value, "media defaultValue", "kind", "id", "documentType", "documentId", "url");
                var kind = String(value, "kind", 10);
                if (kind == "asset")
                {
                    String(value, "id", 500);
                    String(value, "documentType", 500);
                    String(value, "documentId", 500);
                    if (value.TryGetProperty("url", out _)) throw Error("An asset reference cannot contain url.");
                }
                else if (kind == "url")
                {
                    SafeUrl(Property(value, "url", JsonValueKind.String), "url");
                    if (value.TryGetProperty("id", out _) || value.TryGetProperty("documentType", out _) || value.TryGetProperty("documentId", out _))
                        throw Error("A URL reference cannot contain asset identifiers.");
                }
                else throw Error("A media reference kind must be asset or url.");
                break;
            case "segmentation":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id <= 0)
                    throw Error("A segmentation defaultValue must be a positive integer.");
                break;
            case "productSelection":
                var reference = StringValue(value, "defaultValue", 50);
                if (!Guid.TryParse(reference, out var guid) || guid == Guid.Empty)
                    throw Error("A productSelection defaultValue must be a nonempty GUID.");
                break;
        }
    }

    private static decimal Number(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var result))
            throw Error(field + " must be a supported finite number.");
        return result;
    }

    private static void SafeUrl(JsonElement value, string field)
    {
        var text = StringValue(value, field, 2048);
        if (text.Any(char.IsControl) || text.Contains('\\') || text != text.Trim()
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !Uri.IsWellFormedUriString(text, UriKind.Absolute))
            throw Error(field + " must be an absolute safe HTTP(S) URL.");
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Error("Duplicate manifest property: " + property.Name);
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDuplicates(child);
    }

    private static void Object(JsonElement element, string context, params string[] allowed)
    {
        Kind(element, JsonValueKind.Object, context);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Error("Unknown property in " + context + ": " + property.Name);
    }

    private static JsonElement Property(JsonElement parent, string name, JsonValueKind kind)
    {
        if (!parent.TryGetProperty(name, out var value)) throw Error("Missing required property: " + name);
        Kind(value, kind, name);
        return value;
    }

    private static void Kind(JsonElement value, JsonValueKind kind, string field)
    {
        if (value.ValueKind != kind) throw Error(field + " must be " + kind + ".");
    }

    private static string String(JsonElement parent, string name, int max)
        => StringValue(Property(parent, name, JsonValueKind.String), name, max);

    private static string StringValue(JsonElement value, string field, int max, bool nonblank = true)
    {
        Kind(value, JsonValueKind.String, field);
        var text = value.GetString()!;
        if (text.Length > max || (nonblank && string.IsNullOrWhiteSpace(text))) throw Error("Invalid " + field + ".");
        return text;
    }

    private static void UniqueCode(JsonElement parent, string name, HashSet<string> seen)
    {
        var value = String(parent, name, 100);
        CheckCode(value, name);
        if (!seen.Add(value)) throw Error("Duplicate " + name + ": " + value);
    }

    private static void CheckCode(string value, string field)
    {
        if (!Code.IsMatch(value)) throw Error("Invalid " + field + ": " + value);
    }

    private static ThemePackagerException Error(string message) => new(message);

    private sealed class BoundedArchiveStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value)
        {
            if (value > MaxArchive) throw Error("The ZIP exceeds 100000000 bytes.");
            inner.SetLength(value);
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > MaxArchive - inner.Position) throw Error("The ZIP exceeds 100000000 bytes.");
            inner.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > MaxArchive - inner.Position) throw Error("The ZIP exceeds 100000000 bytes.");
            inner.Write(buffer);
        }
    }
}
