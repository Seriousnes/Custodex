using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var opts = Options.Parse(args);
return opts.SelfTest ? SelfTests.Run() : Releaser.Run(opts);

sealed record Options(bool DryRun, bool SelfTest, bool NoRelease)
{
    public static Options Parse(string[] args)
    {
        bool dry = false, self = false, noRel = false;
        foreach (var a in args)
        {
            switch (a)
            {
                case "--dry-run": dry = true; break;
                case "--selftest": self = true; break;
                case "--no-release": noRel = true; break;
                default: throw new ArgumentException($"unknown argument: {a}");
            }
        }
        return new Options(dry, self, noRel);
    }
}

enum BumpLevel { None = 0, Patch = 1, Minor = 2, Major = 3 }

readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public static SemVer Parse(string s)
    {
        var core = s.Split('-', '+')[0].Split('.');
        return new SemVer(
            int.Parse(core[0], CultureInfo.InvariantCulture),
            core.Length > 1 ? int.Parse(core[1], CultureInfo.InvariantCulture) : 0,
            core.Length > 2 ? int.Parse(core[2], CultureInfo.InvariantCulture) : 0);
    }

    public SemVer Bump(BumpLevel level) => level switch
    {
        BumpLevel.Major => new(Major + 1, 0, 0),
        BumpLevel.Minor => new(Major, Minor + 1, 0),
        BumpLevel.Patch => new(Major, Minor, Patch + 1),
        _ => this,
    };

    public int CompareTo(SemVer other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

static class Conventional
{
    static readonly Regex Header = new(@"^(?<type>[a-zA-Z]+)(?<scope>\([^)]*\))?(?<bang>!)?:", RegexOptions.Compiled);

    public static BumpLevel Level(string subject, string body)
    {
        var m = Header.Match(subject);
        bool breaking = (m.Success && m.Groups["bang"].Value == "!")
            || subject.Contains("BREAKING CHANGE") || subject.Contains("BREAKING-CHANGE")
            || body.Contains("BREAKING CHANGE") || body.Contains("BREAKING-CHANGE");
        if (breaking) return BumpLevel.Major;
        if (!m.Success) return BumpLevel.None;
        return m.Groups["type"].Value.ToLowerInvariant() switch
        {
            "feat" => BumpLevel.Minor,
            "fix" => BumpLevel.Patch,
            _ => BumpLevel.None,
        };
    }
}

sealed record Project(string Name, string CsProjPath, string DirRel, string TagPrefix, IReadOnlyList<string> InternalDeps);

static class Releaser
{
    static readonly SemVer Initial = new(0, 1, 0);

    public static int Run(Options opts)
    {
        string repoRoot = Proc.RunOk("git", "rev-parse", "--show-toplevel");
        Directory.SetCurrentDirectory(repoRoot);

        var projects = TopoSort(Discover(repoRoot));
        Log($"Discovered {projects.Count} packable project(s): {string.Join(", ", projects.Select(p => $"{p.Name} [{p.TagPrefix}]"))}");
        if (opts.DryRun) Log("DRY RUN — no tags, packages, or releases will be created.");

        string head = Proc.RunOk("git", "rev-parse", "HEAD");
        var published = new List<string>();
        var skipped = new List<string>();

        foreach (var p in projects)
        {
            var last = LatestTag(p.TagPrefix);
            SemVer next;
            List<(string subject, string body)> changelog;

            if (last is null)
            {
                next = Initial;
                changelog = [];
                Log($"PUBLISH {p.Name}: {p.TagPrefix}{next}  (initial release)");
            }
            else
            {
                string sinceCommit = Proc.RunOk("git", "rev-list", "-n1", last.Value.tag);
                if (!ChangedSince(sinceCommit, p))
                {
                    skipped.Add($"{p.Name}: no changes since {last.Value.tag}");
                    continue;
                }
                var commits = CommitsTouching(sinceCommit, p);
                var level = commits.Aggregate(BumpLevel.None, (acc, c) => Max(acc, Conventional.Level(c.subject, c.body)));
                if (level == BumpLevel.None)
                {
                    skipped.Add($"{p.Name}: changed since {last.Value.tag} but no feat/fix/breaking commit");
                    continue;
                }
                next = last.Value.version.Bump(level);
                changelog = commits;
                Log($"PUBLISH {p.Name}: {p.TagPrefix}{next}  ({level.ToString().ToLowerInvariant()} bump from {last.Value.version})");
            }

            published.Add($"{p.Name} {p.TagPrefix}{next}");
            if (opts.DryRun) continue;

            string tag = p.TagPrefix + next;
            Proc.RunOk("git", "tag", "-a", tag, "-m", tag, head);

            string outDir = Path.Combine(repoRoot, "artifacts", p.Name);
            Directory.CreateDirectory(outDir);
            Proc.RunOk("dotnet", "pack", p.CsProjPath, "-c", "Release", "--no-restore", "-o", outDir, "-p:ContinuousIntegrationBuild=true", "--nologo");
            foreach (var nupkg in Directory.EnumerateFiles(outDir, "*.nupkg"))
                Proc.RunOk("dotnet", "nuget", "push", nupkg, "--source", "github", "--api-key", Env("GITHUB_TOKEN"), "--skip-duplicate");

            Proc.RunOk("git", "push", "origin", tag);
            if (!opts.NoRelease) TryRelease(tag, changelog);
        }

        Log("");
        Log("=== Summary ===");
        foreach (var line in published) Log($"  published {line}");
        foreach (var line in skipped) Log($"  skipped   {line}");
        if (published.Count == 0) Log("  (no package qualified for a release)");
        return 0;
    }

    static List<Project> Discover(string repoRoot)
    {
        var found = new List<Project>();
        foreach (var csproj in Directory.EnumerateFiles(Path.Combine(repoRoot, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var doc = XDocument.Load(csproj);
            if (!string.Equals(Prop(doc, "IsPackable"), "true", StringComparison.OrdinalIgnoreCase)) continue;

            string name = Path.GetFileNameWithoutExtension(csproj);
            string? prefix = Prop(doc, "MinVerTagPrefix");
            if (string.IsNullOrWhiteSpace(prefix))
                throw new InvalidOperationException(
                    $"Packable project '{name}' has no <MinVerTagPrefix>. Add one (e.g. <MinVerTagPrefix>{name.Split('.')[^1].ToLowerInvariant()}-v</MinVerTagPrefix>) so it gets its own version line.");

            var deps = doc.Descendants("ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => Path.GetFileNameWithoutExtension(s!.Replace('\\', '/'))!)
                .ToList();
            string dirRel = Path.GetRelativePath(repoRoot, Path.GetDirectoryName(csproj)!).Replace('\\', '/');
            found.Add(new Project(name, csproj, dirRel, prefix, deps));
        }

        var packable = found.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return found.Select(p => p with { InternalDeps = p.InternalDeps.Where(packable.Contains).ToList() }).ToList();
    }

    static List<Project> TopoSort(List<Project> projects)
    {
        var byName = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var order = new List<Project>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        void Visit(Project p)
        {
            if (state.TryGetValue(p.Name, out int s))
            {
                if (s == 1) throw new InvalidOperationException($"dependency cycle through {p.Name}");
                return;
            }
            state[p.Name] = 1;
            foreach (var dep in p.InternalDeps)
                if (byName.TryGetValue(dep, out var dp)) Visit(dp);
            state[p.Name] = 2;
            order.Add(p);
        }

        foreach (var p in projects.OrderBy(p => p.Name, StringComparer.Ordinal)) Visit(p);
        return order;
    }

    static (string tag, SemVer version)? LatestTag(string prefix)
    {
        (string tag, SemVer version)? best = null;
        foreach (var line in Proc.RunOk("git", "tag", "--list", prefix + "*").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            SemVer v;
            try { v = SemVer.Parse(line[prefix.Length..]); }
            catch { continue; }
            if (best is null || v.CompareTo(best.Value.version) > 0) best = (line, v);
        }
        return best;
    }

    static string[] PathSpecs(Project p) => [p.DirRel + "/", "Directory.Build.props", "Directory.Packages.props", "*.slnx"];

    static bool ChangedSince(string sinceCommit, Project p)
    {
        string[] args = ["diff", "--name-only", sinceCommit, "HEAD", "--", .. PathSpecs(p)];
        return !string.IsNullOrWhiteSpace(Proc.RunOk("git", args));
    }

    static List<(string subject, string body)> CommitsTouching(string sinceCommit, Project p)
    {
        string[] logArgs = ["log", $"{sinceCommit}..HEAD", "--no-merges", "--format=%H", "--", .. PathSpecs(p)];
        var commits = new List<(string, string)>();
        foreach (var hash in Proc.RunOk("git", logArgs).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string subject = Proc.RunOk("git", "show", "-s", "--format=%s", hash);
            string body = Proc.RunOk("git", "show", "-s", "--format=%b", hash);
            commits.Add((subject, body));
        }
        return commits;
    }

    static void TryRelease(string tag, List<(string subject, string body)> commits)
    {
        string notes = commits.Count == 0 ? "Initial release." : string.Join('\n', commits.Select(c => $"- {c.subject}"));
        var (code, _, err) = Proc.Run("gh", "release", "create", tag, "--title", tag, "--notes", notes);
        if (code != 0) Log($"  (warning: GitHub release for {tag} failed: {err.Trim()})");
    }

    static string? Prop(XDocument doc, string name) =>
        doc.Descendants(name).LastOrDefault(e => e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();

    static BumpLevel Max(BumpLevel a, BumpLevel b) => a > b ? a : b;

    static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"environment variable {name} is not set");

    static void Log(string message) => Console.WriteLine(message);
}

static class Proc
{
    public static (int code, string stdout, string stderr) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {file}");
        string outp = p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outp, err);
    }

    public static string RunOk(string file, params string[] args)
    {
        var (code, outp, err) = Run(file, args);
        if (code != 0) throw new InvalidOperationException($"`{file} {string.Join(' ', args)}` exited {code}\n{err}{outp}");
        return outp.Trim();
    }
}

static class SelfTests
{
    public static int Run()
    {
        int failed = 0;
        void Check(bool ok, string label) { if (!ok) { Console.WriteLine($"FAIL: {label}"); failed++; } }

        Check(SemVer.Parse("1.2.3").Bump(BumpLevel.Patch).ToString() == "1.2.4", "patch bump");
        Check(SemVer.Parse("1.2.3").Bump(BumpLevel.Minor).ToString() == "1.3.0", "minor bump");
        Check(SemVer.Parse("1.2.3").Bump(BumpLevel.Major).ToString() == "2.0.0", "major bump");
        Check(SemVer.Parse("0.1.0").CompareTo(SemVer.Parse("0.10.0")) < 0, "numeric (not lexical) compare");
        Check(SemVer.Parse("core-v1.2.3"["core-v".Length..]).ToString() == "1.2.3", "prefix strip parse");

        Check(Conventional.Level("fix: thing", "") == BumpLevel.Patch, "fix -> patch");
        Check(Conventional.Level("feat: thing", "") == BumpLevel.Minor, "feat -> minor");
        Check(Conventional.Level("feat!: thing", "") == BumpLevel.Major, "feat! -> major");
        Check(Conventional.Level("fix(postgres)!: thing", "") == BumpLevel.Major, "scoped bang -> major");
        Check(Conventional.Level("feat: thing", "BREAKING CHANGE: x") == BumpLevel.Major, "breaking body -> major");
        Check(Conventional.Level("chore: thing", "") == BumpLevel.None, "chore -> none");
        Check(Conventional.Level("docs(x): thing", "") == BumpLevel.None, "docs -> none");
        Check(Conventional.Level("not a conventional subject", "") == BumpLevel.None, "non-conventional -> none");

        Console.WriteLine(failed == 0 ? "selftest: OK" : $"selftest: {failed} check(s) FAILED");
        return failed == 0 ? 0 : 1;
    }
}
