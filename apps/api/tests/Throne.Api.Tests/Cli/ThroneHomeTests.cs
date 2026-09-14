using FluentAssertions;
using Throne.Api.Cli;

namespace Throne.Api.Tests.Cli;

public class ThroneHomeTests
{
    [Fact]
    public void Explicit_override_is_absolute_and_marked_explicit()
    {
        var home = ThroneHome.Resolve("/srv/throne-x");

        home.IsExplicit.Should().BeTrue();
        home.Directory.Should().Be(Path.GetFullPath("/srv/throne-x"));
        home.PidFile.Should().Be(Path.Combine(home.Directory, "throne.pid"));
        home.StateFile.Should().Be(Path.Combine(home.Directory, "throne.daemon.json"));
        home.LogFile.Should().Be(Path.Combine(home.Directory, "throne.log"));
        home.DbPath.Should().Be(Path.Combine(home.Directory, "throne.db"));
        home.WorkspacesRoot.Should().Be(Path.Combine(home.Directory, "workspaces"));
    }

    [Fact]
    public void Relative_override_is_resolved_against_cwd()
    {
        var home = ThroneHome.Resolve("./.throne-agent");

        Path.IsPathRooted(home.Directory).Should().BeTrue();
        home.Directory.Should().EndWith(".throne-agent");
    }

    [Fact]
    public void Default_home_is_under_user_profile_and_not_explicit()
    {
        // Inject "unset" through the env seam instead of mutating the real process
        // environment: THRONE_HOME may legitimately be set by whoever launched the test
        // run (e.g. a Throne session), and this test must not depend on that.
        var home = ThroneHome.Resolve(null, getEnvironmentVariable: _ => null);

        home.IsExplicit.Should().BeFalse();
        home.Directory.Should().Be(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".throne"));
    }

    // Property: whatever THRONE_HOME resolves to, the override always wins and marks the
    // home explicit — a blank/absent value is the only case that falls back to the default.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/srv/throne-a")]
    [InlineData("/Users/someone/.throne-session-xyz")]
    public void Explicitness_tracks_only_the_injected_env_value(string? throneHomeEnv)
    {
        var home = ThroneHome.Resolve(null, getEnvironmentVariable: _ => throneHomeEnv);

        home.IsExplicit.Should().Be(!string.IsNullOrWhiteSpace(throneHomeEnv));
    }
}
