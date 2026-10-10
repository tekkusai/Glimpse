using System.Text.RegularExpressions;

namespace SpacePreview.Tests;

/// <summary>
/// The dev probe's step grammar is a contract with the regression scripts
/// and with every agent that captures the app: a verb they use must be one
/// the runner knows, or a capture silently shows the wrong state.
/// </summary>
public sealed class ProbeGrammarTests
{
    private static readonly string[] DocumentedVerbs =
    [
        "delay", "shot", "fullshot", "size", "show", "evaljs", "welcome", "settingsshot", "warningshot",
        "warningfinalshot", "audit", "buttonaudit", "webshot", "zoomin", "multiselect", "pinnedoff",
        "overlayrace", "sign", "signdate", "signpad", "signsave"
    ];

    [Fact]
    public void EveryVerbTheScriptsUseIsOneTheRunnerKnows()
    {
        var runner = TestSource.Read(@"SpacePreview\Dev\ProbeRunner.cs");
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in new[] { "regression.ps1", "ui-stress.ps1" })
        {
            // A probe call is the line naming --probe plus the lines its
            // argument list continues onto (each ends with a comma).
            var lines = TestSource.Read($@"SpacePreview\scripts\{script}").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("'--probe'", StringComparison.Ordinal))
                {
                    continue;
                }

                AddVerbs(lines[i], used);
                while (lines[i].TrimEnd().EndsWith(',') && i + 1 < lines.Length)
                {
                    AddVerbs(lines[++i], used);
                }
            }
        }

        Assert.NotEmpty(used);
        Assert.All(used, verb => Assert.Contains(verb, DocumentedVerbs));
        Assert.All(DocumentedVerbs, verb => Assert.Contains($"case \"{verb}\":", runner, StringComparison.Ordinal));
    }

    private static void AddVerbs(string line, ISet<string> verbs)
    {
        // A whole argument opens the list, follows a comma, or starts a line;
        // quotes inside an evaljs script are not verbs.
        foreach (Match token in Regex.Matches(line, @"(?:^\s*|@\(|,\s+)['""]([a-z]+)(?::[^'""]*)?['""]"))
        {
            verbs.Add(token.Groups[1].Value);
        }
    }
}
