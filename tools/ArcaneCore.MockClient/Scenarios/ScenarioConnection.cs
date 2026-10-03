using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;

namespace ArcaneCore.MockClient.Scenarios;

/// <summary>A bounded lifecycle reader that counts every consumed world frame.</summary>
internal sealed class ScenarioConnection(WorldClient client)
{
    private const int MaximumPacketsPerStage = 128;

    internal int FramesReceived { get; private set; }

    internal Task SendAsync(WorldOpcode opcode, byte[] payload, CancellationToken cancellationToken)
        => client.SendAsync((ushort)opcode, payload, cancellationToken);

    internal async Task<WorldFrame> ReadAsync(CancellationToken cancellationToken)
    {
        WorldFrame frame = await client.ReadAsync(cancellationToken).ConfigureAwait(false);
        FramesReceived++;
        return frame;
    }

    internal async Task<byte[]> ExpectAsync(WorldOpcode opcode, CancellationToken cancellationToken)
    {
        WorldFrame frame = await ReadAsync(cancellationToken).ConfigureAwait(false);
        ScenarioWire.Require(frame.Opcode == (ushort)opcode,
            $"Expected {opcode}, received world opcode 0x{frame.Opcode:X4}.");
        return frame.Payload;
    }

    internal async Task<byte[]> ReadUntilAsync(WorldOpcode opcode, CancellationToken cancellationToken)
    {
        for (int index = 0; index < MaximumPacketsPerStage; index++)
        {
            WorldFrame frame = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Opcode == (ushort)opcode)
            {
                return frame.Payload;
            }
        }

        throw new MockProtocolException($"Expected {opcode} within {MaximumPacketsPerStage} world packets.");
    }

    internal async Task<MockFieldUpdate> ReadUntilFieldAsync(ulong guid, int field, uint expected, CancellationToken cancellationToken)
    {
        for (int index = 0; index < MaximumPacketsPerStage; index++)
        {
            WorldFrame frame = await ReadAsync(cancellationToken).ConfigureAwait(false);
            byte[]? body = frame.Opcode switch
            {
                (ushort)WorldOpcode.SmsgUpdateObject => frame.Payload,
                (ushort)WorldOpcode.SmsgCompressedUpdateObject => ScenarioWire.InflateUpdate(frame.Payload),
                _ => null,
            };
            if (body is null)
            {
                continue;
            }

            foreach (MockFieldUpdate update in ScenarioWire.FieldUpdates(body))
            {
                if (update.Guid == guid && update.Fields.TryGetValue(field, out uint value))
                {
                    ScenarioWire.Require(value == expected,
                        $"Journal field 0x{field:X} contained {value}, expected {expected}.");
                    return update;
                }
            }
        }

        throw new MockProtocolException($"Journal field 0x{field:X} did not arrive within {MaximumPacketsPerStage} packets.");
    }

    internal async Task CreateCharacterAsync(string name, CancellationToken cancellationToken)
    {
        await SendAsync(WorldOpcode.CmsgCharCreate, ScenarioWire.CharacterCreate(name), cancellationToken).ConfigureAwait(false);
        byte[] result = await ExpectAsync(WorldOpcode.SmsgCharCreate, cancellationToken).ConfigureAwait(false);
        ScenarioWire.Require(result.AsSpan().SequenceEqual(new byte[] { 0x2E }), "Character creation did not succeed.");
    }

    internal async Task<IReadOnlyList<MockCharacter>> EnumerateAsync(CancellationToken cancellationToken)
    {
        await SendAsync(WorldOpcode.CmsgCharEnum, [], cancellationToken).ConfigureAwait(false);
        return ScenarioWire.CharacterList(await ReadUntilAsync(WorldOpcode.SmsgCharEnum, cancellationToken).ConfigureAwait(false));
    }

    internal async Task<MockLogin> LoginAsync(ulong guid, CancellationToken cancellationToken)
    {
        await SendAsync(WorldOpcode.CmsgPlayerLogin, ScenarioWire.Guid(guid), cancellationToken).ConfigureAwait(false);
        MockLocation location = ScenarioWire.LoginLocation(await ExpectAsync(WorldOpcode.SmsgLoginVerifyWorld, cancellationToken).ConfigureAwait(false));
        await ExpectAsync(WorldOpcode.SmsgAccountDataMd5, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(WorldOpcode.SmsgFriendList, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(WorldOpcode.SmsgIgnoreList, cancellationToken).ConfigureAwait(false);

        WorldFrame next = await ReadAsync(cancellationToken).ConfigureAwait(false);
        int motdLines = 0;
        while (next.Opcode == (ushort)WorldOpcode.SmsgMessagechat)
        {
            ScenarioWire.Require(++motdLines <= MaximumPacketsPerStage, "Login exceeded the MOTD packet limit.");
            next = await ReadAsync(cancellationToken).ConfigureAwait(false);
        }

        ScenarioWire.Require(next.Opcode == (ushort)WorldOpcode.SmsgSetRestStart, "Login did not reach the pre-map stage.");
        await ExpectAsync(WorldOpcode.SmsgBindpointupdate, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(WorldOpcode.SmsgTutorialFlags, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(WorldOpcode.SmsgInitialSpells, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(WorldOpcode.SmsgActionButtons, cancellationToken).ConfigureAwait(false);
        await ExpectAsync(WorldOpcode.SmsgInitializeFactions, cancellationToken).ConfigureAwait(false);
        byte[] time = await ExpectAsync(WorldOpcode.SmsgLoginSettimespeed, cancellationToken).ConfigureAwait(false);
        ScenarioWire.Require(time.Length == 8, "Login time-speed reply must contain eight bytes.");

        WorldFrame update = await ReadAsync(cancellationToken).ConfigureAwait(false);
        byte[] body = update.Opcode switch
        {
            (ushort)WorldOpcode.SmsgUpdateObject => update.Payload,
            (ushort)WorldOpcode.SmsgCompressedUpdateObject => ScenarioWire.InflateUpdate(update.Payload),
            _ => throw new MockProtocolException($"Login expected self create, received 0x{update.Opcode:X4}."),
        };
        MockSelfCreate self = ScenarioWire.SelfCreate(body);
        ScenarioWire.Require(self.Guid == guid, "Login self create GUID differs from the requested character.");
        // Nearby NPC visibility updates can follow self create before the post-map stage.
        byte[] states = await ReadUntilAsync(WorldOpcode.SmsgInitWorldStates, cancellationToken).ConfigureAwait(false);
        ScenarioWire.Require(states.Length >= 10, "Login world-state reply is truncated.");
        return new MockLogin(location, self, motdLines);
    }

    internal async Task LogoutAsync(CancellationToken cancellationToken)
    {
        await SendAsync(WorldOpcode.CmsgLogoutRequest, [], cancellationToken).ConfigureAwait(false);
        byte[] response = await ReadUntilAsync(WorldOpcode.SmsgLogoutResponse, cancellationToken).ConfigureAwait(false);
        ScenarioWire.Require(response.AsSpan().SequenceEqual(new byte[] { 0, 0, 0, 0, 1 }),
            "Synthetic logout must succeed immediately with an exact five-byte response.");
        byte[] complete = await ReadUntilAsync(WorldOpcode.SmsgLogoutComplete, cancellationToken).ConfigureAwait(false);
        ScenarioWire.Require(complete.Length == 0, "Logout complete must have an empty body.");
    }
}

internal sealed record MockLogin(MockLocation Location, MockSelfCreate Self, int MotdLines);
