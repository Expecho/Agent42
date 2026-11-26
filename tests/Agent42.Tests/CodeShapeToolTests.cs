using System.Diagnostics;
using System.Text.Json;

namespace Agent42.Tests;

/// <summary>
/// Guards <c>.agentic/scripts/code-shape.mjs</c>, the code-shape measurement tool of the clean-code
/// programme (plan cc-01). The Node script is not covered by coverlet; this file is its coverage.
///
/// <para>Like <see cref="DocPathCheckerTests"/>, these run the real script as a subprocess, against a
/// fixture tree for the exact-number tests and against the real tree for the smoke tests, because a
/// reimplementation of the scanner in C# would keep passing while the shipped script regressed.</para>
///
/// <para>The metric under test is the Sonar S138 one: lines of code are the non-blank, non-comment
/// lines from the method NAME line to its closing line.</para>
/// </summary>
public sealed class CodeShapeToolTests
{
    private const string Fixture = """
        namespace Fixture;

        public sealed class Sample(int a, string b = "x", Dictionary<string, List<int>> map = null) : Base
        {
            // a comment with a brace {
            private readonly int _f = 1;
            public int Prop { get; set; }

            public Sample() : this(1) { }

            [Obsolete("x,y")]
            public int Expr(int x, [FromBody] Func<int, int> f) => f(x)
                + 1;

            public void Block(
                Dictionary<string, int> d,
                int n)
            {
                // comment line

                var s = "{ not a brace } // not a comment";
                /* block
                   comment */
                var t = $"{n} {{";
                Console.WriteLine(s);
            }

            public void Overload(int a) { }
            public void Overload(string a) { }

            public (int A, int B) Tuple(int x) { return (x, x); }
        }

        public interface IPort
        {
            void Send(string to, string body);
        }
        """;

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Agent42.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static (int ExitCode, string Output) RunNode(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(Path.Combine(RepoRoot(), ".agentic", "scripts", "code-shape.mjs"));
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout + stderr.GetAwaiter().GetResult());
    }

    private static JsonDocument RunToJson(string root, string src)
    {
        var temp = Path.Combine(Path.GetTempPath(), "a42-shape-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "src"));
            File.WriteAllText(Path.Combine(temp, "src", "Fixture.cs"), src);
            var json = Path.Combine(temp, "out.json");
            var (code, output) = RunNode(temp, "--root", temp, "--json", json, "--all");
            Assert.True(code == 0, output);
            return JsonDocument.Parse(File.ReadAllText(json));
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static readonly Lazy<JsonDocument> RealTree = new(() =>
    {
        var temp = Path.Combine(Path.GetTempPath(), "a42-shape-real-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var (code, output) = RunNode(RepoRoot(), "--json", temp, "--all");
            Assert.True(code == 0, output);
            return JsonDocument.Parse(File.ReadAllText(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    });

    private static JsonElement Method(JsonDocument doc, string name) =>
        doc.RootElement.GetProperty("sections").GetProperty("src").GetProperty("methods")
            .EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);

    [Fact]
    public void CodeShapeTool_MeasuresKnownFixture()
    {
        using var doc = RunToJson(RepoRoot(), Fixture);

        // Params: primary-ctor-free methods; attributes, default values and generic commas ignored.
        Assert.Equal(0, Method(doc, "Fixture.Sample.Sample").GetProperty("params").GetInt32());
        Assert.Equal(2, Method(doc, "Fixture.Sample.Expr").GetProperty("params").GetInt32());
        Assert.Equal(2, Method(doc, "Fixture.Sample.Block").GetProperty("params").GetInt32());
        Assert.Equal(1, Method(doc, "Fixture.Sample.Tuple").GetProperty("params").GetInt32());
        Assert.Equal(2, Method(doc, "Fixture.IPort.Send").GetProperty("params").GetInt32());

        // Expression-bodied member spanning two lines; the attribute line is not counted.
        Assert.Equal(2, Method(doc, "Fixture.Sample.Expr").GetProperty("loc").GetInt32());
        Assert.Equal(1, Method(doc, "Fixture.Sample.Tuple").GetProperty("loc").GetInt32());

        // The class: primary ctor (3 params) wins over the 0-param ctor; members = 6 methods/ctors + field + property.
        var classes = doc.RootElement.GetProperty("sections").GetProperty("src").GetProperty("classes").EnumerateArray().ToList();
        var sample = classes.Single(c => c.GetProperty("key").GetString() == "Fixture.Sample");
        Assert.Equal(3, sample.GetProperty("ctorParams").GetInt32());
        Assert.Equal(8, sample.GetProperty("members").GetInt32());

        // Overloads get distinct keys (parameter types are part of the key).
        var keys = doc.RootElement.GetProperty("sections").GetProperty("src").GetProperty("methods")
            .EnumerateArray().Select(m => m.GetProperty("key").GetString()!).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Contains("src/Fixture.cs::Fixture.Sample.Overload(int)", keys);
        Assert.Contains("src/Fixture.cs::Fixture.Sample.Overload(string)", keys);
    }

    [Fact]
    public void CodeShapeTool_CountsCodeLinesOnly()
    {
        using var doc = RunToJson(RepoRoot(), Fixture);
        var block = Method(doc, "Fixture.Sample.Block");

        // 3 signature lines + `{` + 3 statements + `}` = 8. The comment line, blank line and both
        // lines of the block comment are not code; the string holding braces and `//` is.
        Assert.Equal(8, block.GetProperty("loc").GetInt32());
        Assert.Equal(12, block.GetProperty("span").GetInt32());
    }

    [Fact]
    public void CodeShapeTool_RunsOverSrc()
    {
        var (code, output) = RunNode(RepoRoot());
        Assert.True(code == 0, output);
        Assert.Contains("## src/", output);
        Assert.Contains("## tools/", output);
        Assert.Contains("## tests/", output);

        var src = RealTree.Value.RootElement.GetProperty("sections").GetProperty("src");
        Assert.True(src.GetProperty("methods").GetArrayLength() > 1000);
        Assert.Contains(src.GetProperty("classes").EnumerateArray(),
            c => c.GetProperty("key").GetString() == "Agent42.Agents.AgentGrain");
    }

    [Fact]
    public void CodeShapeBaseline_KeysResolve()
    {
        // A fresh run: every method key names a file that exists and a method name the file spells.
        // (The committed baseline is a snapshot, so later refactors legitimately remove its keys;
        // plan cc-11 owns refreshing it. What must never rot is that the tool's keys are real.)
        var root = RepoRoot();
        var fresh = RealTree.Value.RootElement.GetProperty("sections");
        foreach (var section in fresh.EnumerateObject())
        {
            var seen = new HashSet<string>();
            foreach (var m in section.Value.GetProperty("methods").EnumerateArray())
            {
                var key = m.GetProperty("key").GetString()!;
                Assert.True(seen.Add(key), $"duplicate key {key}");
                var file = m.GetProperty("file").GetString()!;
                var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"{key}: {file} does not exist");
                var simple = m.GetProperty("name").GetString()!.Split('.').Last().TrimStart('~');
                if (!simple.StartsWith("operator", StringComparison.Ordinal))
                {
                    Assert.Contains(simple, File.ReadAllText(path));
                }
            }
        }

        // The committed baseline is well formed: all three sections, unique keys, key format.
        var baselinePath = Path.Combine(root, ".agentic", "code-shape-baseline.json");
        using var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath));
        var sections = baseline.RootElement.GetProperty("sections");
        foreach (var name in new[] { "src", "tools", "tests" })
        {
            var keys = sections.GetProperty(name).GetProperty("methods").EnumerateArray()
                .Select(m => m.GetProperty("key").GetString()!).ToList();
            Assert.Equal(keys.Count, keys.Distinct().Count());
            Assert.All(keys, k => Assert.StartsWith(name + "/", k));
            Assert.All(keys, k => Assert.Contains("::", k));
        }
    }
}
