using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Shouldly;

namespace Custodex.Core.Tests.Guards;

public class DomainVocabularyGuardTests
{
    private static readonly string[] BannedPatterns =
    [
        @"\bzoo\b", @"\banimals?\b", @"\benclosures?\b", @"\bvets?\b", @"\bquarantine\b",
        @"dispens", @"\bspecies\b", @"\bmacropod\b", @"\bkeepers?\b", @"\bkangaroo\b",
        @"\bwallaby\b", @"\bsydney\b", @"\bmelbourne\b", @"\bdragons?\b", @"medicat",
        @"\bdrugs?\b", @"\bbirds?\b", @"dr-smith", @"\bel-0\d", @"\bsmith\b",
    ];

    private static readonly Regex Banned =
        new(string.Join('|', BannedPatterns), RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string CallerPath([CallerFilePath] string path = "") => path;

    [Fact]
    public void Test_sources_contain_no_industry_vocabulary()
    {
        var self = Path.GetFullPath(CallerPath());
        var testsRoot = FindTestsRoot(self);
        var sep = Path.DirectorySeparatorChar;

        var violations = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);
            if (full == self) continue;
            if (full.Contains($"{sep}obj{sep}") || full.Contains($"{sep}bin{sep}") || full.Contains("(2)"))
                continue;

            scanned++;
            var lines = File.ReadAllLines(full);
            for (var i = 0; i < lines.Length; i++)
            {
                var match = Banned.Match(lines[i]);
                if (match.Success)
                    violations.Add($"{full}:{i + 1}: '{match.Value}'");
            }
        }

        scanned.ShouldBeGreaterThan(30, "Guard scanned too few files — path resolution is likely broken.");
        violations.ShouldBeEmpty(
            "Industry/domain vocabulary found in test sources — generate identifiers via TestWorld instead:"
            + Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    private static string FindTestsRoot(string fromFile)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(fromFile)!);
        while (dir is not null && !string.Equals(dir.Name, "tests", StringComparison.Ordinal))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new DirectoryNotFoundException($"Could not locate the 'tests' root from {fromFile}");
    }
}
