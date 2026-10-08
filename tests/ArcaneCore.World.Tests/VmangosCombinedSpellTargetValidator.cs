using System.Buffers.Binary;
using System.Numerics;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Tests;

/// <summary>
/// The one shared packed GUID that vmangos SpellCastTargets::write emits for combined
/// UNIT with CORPSE or GAMEOBJECT targets (Spells/SpellCastTargetsInfo.cpp:180).
/// wowm expects a GUID for each flag. Its SpellMiss model also omits the
/// reflect-result byte vmangos SpellGo::AppendBodyTo sends after SPELL_MISS_REFLECT.
/// Used only when the wowm reader cannot consume SMSG_SPELL_START/GO.
/// </summary>
internal static class VmangosCombinedSpellTargetValidator
{
    public static bool Validate(WorldOpcode opcode, ReadOnlySpan<byte> body)
    {
        var cursor = new Cursor(body);
        cursor.PackedGuid(); // cast item or caster
        cursor.PackedGuid(); // caster
        cursor.Skip(4); // spell ID
        ushort castFlags = cursor.UInt16();
        bool hadReflect = false;
        if (opcode == WorldOpcode.SmsgSpellStart)
            cursor.Skip(4); // cast time
        else if (opcode == WorldOpcode.SmsgSpellGo)
        {
            int hits = cursor.Byte();
            cursor.Skip(checked(hits * 8));
            int misses = cursor.Byte();
            for (int i = 0; i < misses; i++)
            {
                cursor.Skip(8); // target GUID
                byte reason = cursor.Byte();
                if (reason == 11)
                {
                    cursor.Skip(1); // SPELL_MISS_REFLECT result
                    hadReflect = true;
                }
            }
        }
        else return false;

        ushort targetMask = cursor.UInt16();
        const ushort sharedSecondary = 0x0200 | 0x0800 | 0x8000; // corpse enemy, gameobject, corpse ally
        bool sharedGuid = (targetMask & 0x0002) != 0 && (targetMask & sharedSecondary) != 0
            && (targetMask & ~(0x0002 | sharedSecondary)) == 0;
        bool simpleReflectedTarget = hadReflect && targetMask is 0x0000 or 0x0002;
        if (!sharedGuid && !simpleReflectedTarget) return false;
        if (targetMask != 0) cursor.PackedGuid();
        if ((castFlags & 0x20) != 0) cursor.Skip(8); // CAST_FLAG_AMMO
        if (cursor.Position != body.Length)
            throw new InvalidDataException($"{body.Length - cursor.Position} trailing combined-target bytes");
        return true;
    }

    private ref struct Cursor(ReadOnlySpan<byte> body)
    {
        private readonly ReadOnlySpan<byte> _body = body;
        public int Position { get; private set; }

        public byte Byte()
        {
            Need(1);
            return _body[Position++];
        }

        public ushort UInt16()
        {
            Need(2);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_body[Position..]);
            Position += 2;
            return value;
        }

        public void Skip(int bytes)
        {
            Need(bytes);
            Position += bytes;
        }

        public void PackedGuid() => Skip(BitOperations.PopCount((uint)Byte()));

        private void Need(int bytes)
        {
            if (bytes < 0 || bytes > _body.Length - Position)
                throw new InvalidDataException("truncated combined spell target packet");
        }
    }
}
