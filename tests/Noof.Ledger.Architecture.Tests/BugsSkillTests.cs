using AwesomeAssertions;

namespace Noof.Ledger.Architecture.Tests;

// The /bugs skill reads noof_ledger, the operator's real ledger, so only the operator may start it. Without
// disable-model-invocation the model could start it by itself whenever its description seemed to fit.
public class BugsSkillTests
{
    static readonly string SkillFile = Path.Combine(RepoRoot.Find().FullName, ".claude", "skills", "bugs", "SKILL.md");

    [Fact]
    public void Only_the_operator_can_start_the_bugs_skill()
    {
        File.Exists(SkillFile).Should().BeTrue($"{SkillFile} must exist");

        var frontmatter = Frontmatter(File.ReadAllText(SkillFile));

        frontmatter.Should().Contain("name: bugs");
        frontmatter.Should().Contain("disable-model-invocation: true");
    }

    static string[] Frontmatter(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        lines[0].Should().Be("---", "a skill file starts with its frontmatter");
        var end = Array.IndexOf(lines, "---", 1);
        end.Should().BeGreaterThan(0, "the frontmatter must be closed by a second ---");
        return lines[1..end];
    }
}
