using ArcaneCore.Kernel.Configuration.Conf;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Configuration.Conf;

public sealed class ConfSecretsTests
{
    [Theory]
    [InlineData("Database:Auth:Password", "hunter2", ConfSecrets.Placeholder)]
    [InlineData("World:Playerbots:Qa:GitHub:Token", "ghp_x", ConfSecrets.Placeholder)]
    [InlineData("World:Playerbots:Chat:ApiKey", "sk-x", ConfSecrets.Placeholder)]
    [InlineData("World:Playerbots:Qa:GitHub:GhPassToken", "false", "false")]
    [InlineData("World:Playerbots:Qa:GitHub:GhPassToken", "True", "True")]
    [InlineData("World:Playerbots:Qa:GitHub:TokenEnvironmentVariable", "ARCANECORE_GITHUB_TOKEN", "ARCANECORE_GITHUB_TOKEN")]
    [InlineData("World:Playerbots:Qa:GitHub:Token", "", "")]
    public void Mask_HidesSecretValues_ButNotSwitchesOrVariableNames(string path, string value, string expected)
    {
        Assert.Equal(expected, ConfSecrets.Mask(path, value));
    }

    [Fact]
    public void ATokenNamedValueThatIsNotABoolean_IsStillMasked()
    {
        Assert.Equal(ConfSecrets.Placeholder, ConfSecrets.Mask("Some:ApiToken", "truthy"));
        Assert.True(ConfSecrets.IsSecretPath("Some:ApiToken"));
        Assert.False(ConfSecrets.IsSecretPath("World:Playerbots:Qa:GitHub:TokenEnvironmentVariable"));
    }
}
