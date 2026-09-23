using System.IO;
namespace OracleHost.Services;

/// <summary>
/// Field-by-field inspection of an AccountInfo folder. Every checked field is
/// reported (found or missing) so the startup dialog can show exactly what the
/// bundle contains. No key contents are ever included in the report — only file
/// names and whether values are present.
/// </summary>
public class AccountInfoReport
{
    /// <summary>A field the parser knows how to read.</summary>
    public record Field(string Label, bool Present, string Value, bool Required);

    public string Folder { get; init; } = "";
    public bool MetadataFileExists { get; init; }
    public int PrivateKeyCount { get; init; }
    public int PublicKeyCount { get; init; }
    /// <summary>File name only — never the key contents.</summary>
    public string? PrivateKeyName { get; init; }
    /// <summary>File name only — never the key contents.</summary>
    public string? PublicKeyName { get; init; }
    public List<Field> Fields { get; init; } = new();

    public bool MetadataUsable => MetadataFileExists && Fields.All(f => !f.Required || f.Present);
    public bool HasExactlyOnePrivateKey => PrivateKeyCount == 1;
    public bool BundleUsable => MetadataUsable && HasExactlyOnePrivateKey && PublicKeyCount <= 1;

    public string Describe()
    {
        var lines = new List<string> { $"AccountInfo folder: {Folder}" };
        lines.Add(MetadataFileExists
            ? "information.md: found"
            : "information.md: MISSING");

        foreach (var field in Fields)
        {
            var state = field.Present ? "found" : "MISSING";
            var requirement = field.Required ? "required" : "optional";
            // Show the value only for harmless optional fields; OCIDs are
            // identifiers, so summarize rather than print them in full.
            var detail = field.Present &&
                         (field.Label == "Home region" || field.Label == "Fingerprint")
                ? $" — {field.Value}"
                : "";
            lines.Add($"{field.Label} ({requirement}): {state}{detail}");
        }

        if (PrivateKeyCount == 1)
            lines.Add($"Private key: found — {PrivateKeyName}");
        else
            lines.Add($"Private key: MISSING — expected exactly one, found {PrivateKeyCount}");

        lines.Add(PublicKeyCount switch
        {
            0 => "Public key: not present (optional)",
            1 => $"Public key: found — {PublicKeyName}",
            _ => $"Public key: TOO MANY — expected at most one, found {PublicKeyCount}"
        });

        return string.Join(Environment.NewLine, lines);
    }

    public List<string> MissingSummary()
    {
        var problems = new List<string>();
        if (!MetadataFileExists)
            problems.Add("information.md");
        problems.AddRange(Fields.Where(f => f.Required && !f.Present).Select(f => f.Label));
        if (!HasExactlyOnePrivateKey)
            problems.Add($"exactly one private PEM (found {PrivateKeyCount})");
        if (PublicKeyCount > 1)
            problems.Add($"at most one public PEM (found {PublicKeyCount})");
        return problems;
    }

    /// <summary>
    /// Inspects the AccountInfo folder without throwing and without reading any
    /// key material. Fingerprint mismatches and PEM parse errors are not part
    /// of this report — it answers only "what is present?".
    /// </summary>
    public static AccountInfoReport Inspect(string accountDirectory)
    {
        var directory = Path.GetFullPath(accountDirectory);
        var metadataPath = Path.Combine(directory, "information.md");
        var metadataFileExists = File.Exists(metadataPath);
        var directoryExists = Directory.Exists(directory);

        var privateKeys = directoryExists
            ? Directory.EnumerateFiles(directory, "*.pem")
                .Where(path => !Path.GetFileName(path)
                    .Contains("public", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();
        var publicKeys = directoryExists
            ? Directory.EnumerateFiles(directory, "*public*.pem")
                .ToArray()
            : Array.Empty<string>();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metadataFileExists)
        {
            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Tenancy OCID", "Home region", "User OCID", "Compartment OCID",
                "Subnet OCID", "Fingerprint"
            };
            string? currentLabel = null;
            foreach (var rawLine in File.ReadAllLines(metadataPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (labels.Contains(line))
                {
                    currentLabel = line;
                    continue;
                }
                if (currentLabel != null && !values.ContainsKey(currentLabel))
                    values[currentLabel] = line;
            }
        }

        Field Read(string label, bool required) =>
            new(label,
                values.TryGetValue(label, out var value) && !string.IsNullOrWhiteSpace(value),
                values.TryGetValue(label, out value) ? value.Trim() : "",
                required);

        var fields = new List<Field>
        {
            Read("Tenancy OCID", required: true),
            Read("User OCID", required: true),
            Read("Compartment OCID", required: true),
            Read("Subnet OCID", required: true),
            Read("Home region", required: false),
            Read("Fingerprint", required: false),
        };

        return new AccountInfoReport
        {
            Folder = directory,
            MetadataFileExists = metadataFileExists,
            PrivateKeyCount = privateKeys.Length,
            PublicKeyCount = publicKeys.Length,
            PrivateKeyName = privateKeys.Length == 1 ? Path.GetFileName(privateKeys[0]) : null,
            PublicKeyName = publicKeys.Length == 1 ? Path.GetFileName(publicKeys[0]) : null,
            Fields = fields,
        };
    }
}
