using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Khors.Ipc;
using Khors.Platform.Windows.Ipc;
using Xunit;

namespace Khors.Platform.Windows.Tests;

public class NamedPipeIpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => "KHORS-test-" + Guid.NewGuid().ToString("N");

    private static NamedPipeIpcServer Server(string name) => new(name, WindowsIdentity.GetCurrent().User);

    [Fact]
    public async Task ClientAndServerExchangeFramesAndServerSeesClientProcess()
    {
        var name = UniqueName();
        await using var server = Server(name);
        var accept = server.AcceptAsync(Ct);
        var client = new NamedPipeIpcClient(name, pid => pid == Environment.ProcessId);

        await using var clientStream = await client.ConnectAsync(TimeSpan.FromSeconds(5), Ct);
        await using var connection = await accept;

        Assert.Equal(Environment.ProcessId, connection.Peer.ProcessId);
        Assert.Equal(WindowsIdentity.GetCurrent().Name.Split('\\')[^1], connection.Peer.UserName, ignoreCase: true);

        await IpcFraming.WriteFrameAsync(clientStream, "{\"ping\":1}"u8.ToArray(), Ct);
        Assert.Equal("{\"ping\":1}"u8.ToArray(), await IpcFraming.ReadFrameAsync(connection.Stream, Ct));
        await IpcFraming.WriteFrameAsync(connection.Stream, "{\"pong\":1}"u8.ToArray(), Ct);
        Assert.Equal("{\"pong\":1}"u8.ToArray(), await IpcFraming.ReadFrameAsync(clientStream, Ct));
    }

    [Fact]
    public async Task ServerAcceptsSeveralClients()
    {
        var name = UniqueName();
        await using var server = Server(name);
        var client = new NamedPipeIpcClient(name, _ => true);

        var firstAccept = server.AcceptAsync(Ct);
        await using var first = await client.ConnectAsync(TimeSpan.FromSeconds(5), Ct);
        await using var firstConnection = await firstAccept;

        var secondAccept = server.AcceptAsync(Ct);
        await using var second = await client.ConnectAsync(TimeSpan.FromSeconds(5), Ct);
        await using var secondConnection = await secondAccept;

        Assert.NotSame(firstConnection.Stream, secondConnection.Stream);
    }

    [Fact]
    public async Task ClientRejectsUntrustedServerProcess()
    {
        var name = UniqueName();
        await using var server = Server(name);
        var accept = server.AcceptAsync(Ct);

        var client = new NamedPipeIpcClient(name, _ => false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ConnectAsync(TimeSpan.FromSeconds(5), Ct));
        (await accept).DisposeAsync().AsTask().Wait(Ct);
    }

    [Fact]
    public async Task ServerRefusesNameTakenByAnotherPipe()
    {
        var name = UniqueName();
        await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var server = Server(name);

        await Assert.ThrowsAnyAsync<Exception>(() => server.AcceptAsync(Ct));
    }

    [Fact]
    public async Task MissingServiceIsTimeout()
    {
        var client = new NamedPipeIpcClient(UniqueName(), _ => true);

        await Assert.ThrowsAsync<TimeoutException>(() => client.ConnectAsync(TimeSpan.FromMilliseconds(300), Ct));
    }

    [Fact]
    public void PipeAclAllowsOnlyLocalInteractiveUsersToReadWrite()
    {
        var rules = NamedPipeIpcServer.CreateSecurity(ownerFullControl: null)
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        PipeAccessRule Rule(WellKnownSidType sid, AccessControlType type) =>
            Assert.Single(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(sid, null)) && r.AccessControlType == type);

        Assert.Equal(PipeAccessRights.FullControl, Rule(WellKnownSidType.NetworkSid, AccessControlType.Deny).PipeAccessRights);
        var interactive = Rule(WellKnownSidType.InteractiveSid, AccessControlType.Allow).PipeAccessRights;
        Assert.True(interactive.HasFlag(PipeAccessRights.ReadWrite));
        Assert.False(interactive.HasFlag(PipeAccessRights.CreateNewInstance), "Пользователи не должны создавать экземпляры канала службы.");
        Assert.False(interactive.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.Equal(PipeAccessRights.FullControl, Rule(WellKnownSidType.LocalSystemSid, AccessControlType.Allow).PipeAccessRights);

        // Больше никому доступ не выдан (в частности, Everyone и Authenticated Users).
        Assert.Equal(4, rules.Count);
    }
}
