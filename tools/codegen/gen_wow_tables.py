#!/usr/bin/env python3
"""Generate ArcaneCore's build-5875 protocol tables from the reference checkouts.

Outputs (checked in, regenerate after changing this script):
  src/ArcaneCore.Protocol/WorldOpcode.g.cs  -- every 1.12.1 world opcode + wire names
  src/ArcaneCore.Game/UpdateFields.g.cs     -- every 1.12.1 update field + visibility flags

Sources and cross-checks (charter §1.1 / §4):
  * vmangos  src/game/Server/Protocol/Opcodes_1_12_1.h   (names + values, all 825 opcodes)
  * vmangos  src/game/Objects/UpdateFields_1_12_1.cpp    (names, offsets, sizes, types, flags)
  * gtker/wow_messages intermediate_representation.json  (MIT/Apache) -- every opcode and every
    update-field range it defines for 1.12 must agree with vmangos, or generation fails.

Only facts (identifiers, numbers, flags) are taken; no code is copied.

Usage:
  python3 tools/codegen/gen_wow_tables.py --vmangos <vmangos/core> --wow-messages <wow_messages>
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

TYPEMASK_TO_GTKER = {
    "TYPEMASK_OBJECT": "Object",
    "TYPEMASK_ITEM": "Item",
    "TYPEMASK_CONTAINER": "Container",
    "TYPEMASK_UNIT": "Unit",
    "TYPEMASK_PLAYER": "Player",
    "TYPEMASK_GAMEOBJECT": "GameObject",
    "TYPEMASK_DYNAMICOBJECT": "DynamicObject",
    "TYPEMASK_CORPSE": "Corpse",
}

# vmangos UpdateFieldFlags (UpdateFields.h) -> C# enum member names.
FLAG_NAMES = {
    "UF_FLAG_NONE": "None",
    "UF_FLAG_PUBLIC": "Public",
    "UF_FLAG_PRIVATE": "Private",
    "UF_FLAG_OWNER_ONLY": "OwnerOnly",
    "UF_FLAG_UNK1": "Unk1",
    "UF_FLAG_UNK2": "ItemOwner",
    "UF_FLAG_SPECIAL_INFO": "SpecialInfo",
    "UF_FLAG_GROUP_ONLY": "GroupOnly",
    "UF_FLAG_UNK5": "Unk5",
    "UF_FLAG_DYNAMIC": "Dynamic",
}

FLAG_VALUES = {
    "UF_FLAG_NONE": 0x000,
    "UF_FLAG_PUBLIC": 0x001,
    "UF_FLAG_PRIVATE": 0x002,
    "UF_FLAG_OWNER_ONLY": 0x004,
    "UF_FLAG_UNK1": 0x008,
    "UF_FLAG_UNK2": 0x010,
    "UF_FLAG_SPECIAL_INFO": 0x020,
    "UF_FLAG_GROUP_ONLY": 0x040,
    "UF_FLAG_UNK5": 0x080,
    "UF_FLAG_DYNAMIC": 0x100,
}

TYPE_MAP = {
    "UF_TYPE_NONE": "None",
    "UF_TYPE_INT": "Int",
    "UF_TYPE_TWO_SHORT": "TwoShort",
    "UF_TYPE_FLOAT": "Float",
    "UF_TYPE_GUID": "Guid",
    "UF_TYPE_BYTES": "Bytes",
    "UF_TYPE_BYTES2": "Bytes",
}

# vmangos value types accepted for each gtker update-mask data type. ArrayOfStruct ranges
# (visible items, skill info) mix member types, so only their extent is checked.
GTKER_TYPE_OK = {
    "Int": {"Int", "TwoShort", "Bytes"},
    "Float": {"Float"},
    "Guid": {"Guid"},
    "GuidArrayUsingEnum": {"Guid"},
    "Bytes": {"Bytes", "Int"},
    "TwoShort": {"TwoShort", "Int"},
}


def pascal(name: str) -> str:
    parts = [p for p in name.split("_") if p]
    return "".join(p[:1].upper() + p[1:].lower() if not p.isdigit() else p for p in parts)


def fail(message: str) -> None:
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


# --------------------------------------------------------------------------- opcodes


def load_vmangos_opcodes(vmangos: Path) -> list[tuple[str, int]]:
    path = vmangos / "src/game/Server/Protocol/Opcodes_1_12_1.h"
    out: list[tuple[str, int]] = []
    for line in path.read_text().splitlines():
        m = re.match(r"\s*([A-Z0-9_]+)\s*=\s*(0x[0-9A-Fa-f]+|\d+)\s*,?", line)
        if m and m.group(1) != "NUM_MSG_TYPES":
            out.append((m.group(1), int(m.group(2), 0)))
    if len(out) < 800:
        fail(f"only {len(out)} opcodes parsed from {path}")
    return out


def has_version_112(tags: dict) -> bool:
    version = tags.get("version", {}).get("version_type", {})
    if version.get("world_version_tag") == "all":
        return True
    for v in version.get("versions", []):
        if v["major"] == 1 and (v["minor"] is None or (v["minor"] == 12 and v["patch"] in (None, 1))):
            return True
    return False


def check_opcodes_against_gtker(opcodes: list[tuple[str, int]], ir: dict) -> int:
    by_name = dict(opcodes)
    by_value: dict[int, list[str]] = {}
    for name, value in opcodes:
        by_value.setdefault(value, []).append(name)
    checked = 0
    for message in ir["world"]["messages"]:
        if not has_version_112(message["tags"]):
            continue
        name = message["name"]
        for suffix in ("_Client", "_Server"):
            if name.endswith(suffix):
                name = name[: -len(suffix)]
        opcode = message["object_type"]["opcode"]
        if name in by_name:
            if by_name[name] != opcode:
                fail(f"opcode mismatch for {name}: vmangos {by_name[name]} vs gtker {opcode}")
        elif opcode not in by_value:
            fail(f"gtker defines {name}={opcode} for 1.12 but vmangos has no opcode with that value")
        else:
            # Same value, different spelling (e.g. gtker SMSG_ACCOUNT_DATA_TIMES is vmangos
            # SMSG_ACCOUNT_DATA_MD5). The value is what goes on the wire; keep vmangos' name.
            print(f"note: gtker {name} = vmangos {by_value[opcode][0]} ({opcode})")
        checked += 1
    return checked


def emit_opcodes(opcodes: list[tuple[str, int]], checked: int) -> str:
    max_value = max(v for _, v in opcodes)
    lines = [
        "// <auto-generated>",
        "// Generated by tools/codegen/gen_wow_tables.py -- do not edit by hand.",
        "// Source: vmangos Opcodes_1_12_1.h (all build-5875 world opcodes); every opcode that",
        f"// gtker/wow_messages defines for 1.12 ({checked} messages) was verified to match.",
        "// </auto-generated>",
        "",
        "#nullable enable",
        "",
        "namespace ArcaneCore.Protocol;",
        "",
        "/// <summary>World-stream opcodes for client build 5875 (2 bytes server→client, 4 bytes client→server).</summary>",
        "public enum WorldOpcode : ushort",
        "{",
    ]
    for name, value in opcodes:
        lines.append(f"    /// <summary>{name}</summary>")
        lines.append(f"    {pascal(name)} = {value},")
    lines += [
        "}",
        "",
        "/// <summary>Wire names (as in the client and the references) for logging.</summary>",
        "public static class WorldOpcodeNames",
        "{",
        f"    private static readonly string?[] Names = new string?[{max_value + 1}];",
        "",
        "    static WorldOpcodeNames()",
        "    {",
    ]
    for name, value in opcodes:
        lines.append(f'        Names[{value}] = "{name}";')
    lines += [
        "    }",
        "",
        "    /// <summary>The reference name of an opcode, or its hex value when unknown.</summary>",
        "    public static string GetName(WorldOpcode opcode)",
        "    {",
        "        int index = (int)opcode;",
        '        return index < Names.Length && Names[index] is { } name ? name : $"0x{index:X3}";',
        "    }",
        "",
        "    /// <summary>The reference name of a raw opcode value.</summary>",
        "    public static string GetName(uint opcode)",
        '        => opcode < (uint)Names.Length && Names[opcode] is { } name ? name : $"0x{opcode:X3}";',
        "}",
        "",
    ]
    return "\n".join(lines)


# --------------------------------------------------------------------------- update fields


def load_vmangos_fields(vmangos: Path) -> list[dict]:
    path = vmangos / "src/game/Objects/UpdateFields_1_12_1.cpp"
    text = path.read_text()
    rows = re.findall(
        r'\{\s*(TYPEMASK_[A-Z_]+)\s*,\s*"([A-Z0-9_]+)"\s*,\s*(0x[0-9A-Fa-f]+|\d+)\s*,\s*(\d+)\s*,'
        r'\s*(UF_TYPE_[A-Z0-9_]+)\s*,\s*([A-Z0-9_ +|]+?)\s*\}',
        text,
    )
    fields = []
    for mask, name, offset, size, vtype, flags in rows:
        flag_value = 0
        flag_names = []
        for part in re.split(r"[+|]", flags):
            part = part.strip()
            if part not in FLAG_VALUES:
                fail(f"unknown flag {part} on {name}")
            flag_value |= FLAG_VALUES[part]
            if FLAG_VALUES[part]:
                flag_names.append(FLAG_NAMES[part])
        fields.append(
            {
                "mask": mask,
                "name": name,
                "offset": int(offset, 0),
                "size": int(size),
                "type": TYPE_MAP[vtype],
                "flags": flag_value,
                "flag_names": flag_names or ["None"],
            }
        )
    if len(fields) != 324:
        fail(f"expected 324 update-field rows in {path}, parsed {len(fields)}")
    return fields


def check_fields_against_gtker(fields: list[dict], ir: dict) -> int:
    """Every gtker range must be tiled exactly by vmangos ranges of a compatible type."""
    by_type: dict[str, list[dict]] = {}
    for f in fields:
        if f["size"] == 0:
            continue
        by_type.setdefault(TYPEMASK_TO_GTKER[f["mask"]], []).append(f)
    checked = 0
    for g in ir["vanilla_update_mask"]:
        gtype = g["object_type"]
        start, end = g["offset"], g["offset"] + g["size"]
        data_type = g["data_type"]["update_mask_type_tag"]
        covered = [f for f in by_type.get(gtype, []) if f["offset"] < end and f["offset"] + f["size"] > start]
        if not covered:
            fail(f"gtker {gtype}.{g['name']} [{start:#x},{end:#x}) has no vmangos field")
        lo = min(f["offset"] for f in covered)
        hi = max(f["offset"] + f["size"] for f in covered)
        if lo != start or hi != end or sum(f["size"] for f in covered) != g["size"]:
            # gtker splits UNIT_FIELD_RESISTANCES into seven named entries; vmangos keeps one
            # 7-wide array. A gtker range lying wholly inside one vmangos range is consistent.
            if not (len(covered) == 1 and covered[0]["offset"] <= start and covered[0]["offset"] + covered[0]["size"] >= end):
                fail(f"gtker {gtype}.{g['name']} [{start:#x},{end:#x}) not tiled by vmangos {[c['name'] for c in covered]}")
        if data_type != "ArrayOfStruct":
            if data_type not in GTKER_TYPE_OK:
                fail(f"unknown gtker data type {data_type} on {gtype}.{g['name']}")
            for f in covered:
                if f["type"] not in GTKER_TYPE_OK[data_type]:
                    fail(f"type mismatch {f['name']} {f['type']} vs gtker {gtype}.{g['name']} {data_type}")
        checked += 1
    return checked


def emit_fields(fields: list[dict], checked: int) -> str:
    ends = {f["name"]: f["offset"] for f in fields if f["name"].endswith("_END")}
    lines = [
        "// <auto-generated>",
        "// Generated by tools/codegen/gen_wow_tables.py -- do not edit by hand.",
        "// Source: vmangos UpdateFields_1_12_1.cpp (the client's own field descriptor table for",
        f"// build 5875); all {checked} gtker/wow_messages 1.12 update-mask ranges were verified",
        "// to be tiled by these fields with matching offsets, sizes and types.",
        "// </auto-generated>",
        "",
        "#nullable enable",
        "",
        "namespace ArcaneCore.Game;",
        "",
        "/// <summary>Absolute update-field indices for build 5875.</summary>",
        "public static class UpdateFields",
        "{",
    ]
    for f in fields:
        size_note = f" (size {f['size']})" if f["size"] > 1 else ""
        lines.append(f"    /// <summary>{f['name']}{size_note}; {', '.join(f['flag_names'])}.</summary>")
        lines.append(f"    public const int {pascal(f['name'])} = 0x{f['offset']:X};")
    lines += ["}", ""]

    lines += [
        "/// <summary>",
        "/// Who may receive an update field (vmangos UpdateFieldFlags). <see cref=\"ItemOwner\"/> is",
        "/// vmangos UF_FLAG_UNK2, which it grants together with OwnerOnly to an item's owner.",
        "/// </summary>",
        "[Flags]",
        "public enum UpdateFieldFlags : ushort",
        "{",
    ]
    for key, value in FLAG_VALUES.items():
        lines.append(f"    {FLAG_NAMES[key]} = 0x{value:03X},")
    lines += ["}", ""]

    tables = [
        ("Container", "ContainerEnd", ["TYPEMASK_OBJECT", "TYPEMASK_ITEM", "TYPEMASK_CONTAINER"],
         "Items and containers (indexed up to CONTAINER_END)."),
        ("Unit", "PlayerEnd", ["TYPEMASK_OBJECT", "TYPEMASK_UNIT", "TYPEMASK_PLAYER"],
         "Units and players (indexed up to PLAYER_END)."),
        ("GameObject", "GameobjectEnd", ["TYPEMASK_OBJECT", "TYPEMASK_GAMEOBJECT"],
         "Game objects (indexed up to GAMEOBJECT_END)."),
        ("DynamicObject", "DynamicobjectEnd", ["TYPEMASK_OBJECT", "TYPEMASK_DYNAMICOBJECT"],
         "Dynamic objects (indexed up to DYNAMICOBJECT_END)."),
        ("Corpse", "CorpseEnd", ["TYPEMASK_OBJECT", "TYPEMASK_CORPSE"],
         "Corpses (indexed up to CORPSE_END)."),
    ]
    end_names = {
        "ContainerEnd": "CONTAINER_END",
        "PlayerEnd": "PLAYER_END",
        "GameobjectEnd": "GAMEOBJECT_END",
        "DynamicobjectEnd": "DYNAMICOBJECT_END",
        "CorpseEnd": "CORPSE_END",
    }
    lines += [
        "/// <summary>Per-object-type visibility flags and GUID-pair starts, one entry per field index.</summary>",
        "public static class UpdateFieldTables",
        "{",
    ]
    for label, end_const, masks, doc in tables:
        size = ends[end_names[end_const]]
        flags = [0] * size
        guid_start = [False] * size
        for f in fields:
            if f["mask"] not in masks or f["size"] == 0:
                continue
            for i in range(f["offset"], f["offset"] + f["size"]):
                flags[i] = f["flags"]
            if f["type"] == "Guid":
                for i in range(f["offset"], f["offset"] + f["size"] - 1, 2):
                    guid_start[i] = True
        lines.append(f"    /// <summary>Visibility flags — {doc}</summary>")
        lines.append(f"    public static ReadOnlySpan<ushort> {label}Visibility => new ushort[]")
        lines.append("    {")
        for i in range(0, size, 16):
            chunk = ", ".join(f"0x{v:03X}" for v in flags[i : i + 16])
            lines.append(f"        {chunk},")
        lines.append("    };")
        lines.append("")
        lines.append(f"    /// <summary>True at the low half of each 64-bit GUID field — {doc}</summary>")
        lines.append(f"    public static ReadOnlySpan<bool> {label}GuidStarts => new bool[]")
        lines.append("    {")
        for i in range(0, size, 16):
            chunk = ", ".join("true" if v else "false" for v in guid_start[i : i + 16])
            lines.append(f"        {chunk},")
        lines.append("    };")
        lines.append("")
    lines[-1:] = ["}", ""]
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--vmangos", type=Path, required=True)
    parser.add_argument("--wow-messages", type=Path, required=True)
    args = parser.parse_args()

    ir = json.loads((args.wow_messages / "intermediate_representation.json").read_text())

    opcodes = load_vmangos_opcodes(args.vmangos)
    checked_opcodes = check_opcodes_against_gtker(opcodes, ir)
    (REPO / "src/ArcaneCore.Protocol/WorldOpcode.g.cs").write_text(emit_opcodes(opcodes, checked_opcodes))

    fields = load_vmangos_fields(args.vmangos)
    checked_fields = check_fields_against_gtker(fields, ir)
    (REPO / "src/ArcaneCore.Game/UpdateFields.g.cs").write_text(emit_fields(fields, checked_fields))

    print(f"opcodes: {len(opcodes)} written, {checked_opcodes} cross-checked against gtker")
    print(f"update fields: {len(fields)} written, {checked_fields} gtker ranges cross-checked")


if __name__ == "__main__":
    main()
