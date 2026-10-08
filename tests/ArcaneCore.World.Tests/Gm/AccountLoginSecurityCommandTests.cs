using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.LogonSecurity;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

public sealed class AccountLoginSecurityCommandTests
{
    [Theory]
    [InlineData("account pin set TEST")]
    [InlineData("account pin clear TEST")]
    [InlineData("account totp set TEST")]
    [InlineData("account totp clear TEST")]
    [InlineData("account iplock TEST on")]
    public void FactorCommands_RequireAdministrator(string text)
    {
        var table = new CommandTable(new AccountLoginSecurityCommands().Commands);
        CommandLookup admin = table.Lookup(text, AccountSecurity.Administrator);
        Assert.Equal(CommandLookupResult.Ok, admin.Result);
        Assert.True(admin.Available);
        CommandLookup gm = table.Lookup(text, AccountSecurity.GameMaster);
        Assert.False(gm.Result == CommandLookupResult.Ok && gm.Available);
    }
}
