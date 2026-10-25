#!/usr/bin/env python3
"""Normalise TypeDef visibility flags in a generated il2cpp assembly.

Cpp2IL / Il2CppInterop occasionally emits a TypeDef whose TypeAttributes claim a
*nested* visibility (NestedPublic, NestedPrivate, ...) while the NestedClass table
has no row for it. The CLR then refuses to load the type:

    TypeLoadException: Could not load type 'X' from assembly 'Y'
    because the format is invalid.

`Assembly.GetTypes()` does not merely throw on such a type - it aborts the process
with an uncatchable "Fatal error. Invalid Program" and takes the whole game with it.
A single such type is enough to kill every reflection-based mod.

This tool rewrites only the visibility bits (mask 0x7) of the offending rows,
leaving every other bit, and every other table, untouched. Row size never changes,
so all metadata tokens stay valid.

Usage:
    python patch_typeflags.py <assembly.dll> [--dry-run]
"""
import os
import shutil
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from mdcheck import Meta

VISIBILITY = {
    0: "NotPublic",
    1: "Public",
    2: "NestedPublic",
    3: "NestedPrivate",
    4: "NestedFamily",
    5: "NestedAssembly",
    6: "NestedFamANDAssem",
    7: "NestedFamORAssem",
}

# A top-level type may only be Public or NotPublic. Anything that asked to be
# nested-but-public becomes Public; anything that asked to be nested-and-hidden
# becomes NotPublic. This mirrors what the compiler would have emitted.
FIX = {
    2: 1,  # NestedPublic       -> Public
    4: 1,  # NestedFamily       -> Public
    7: 1,  # NestedFamORAssem   -> Public
    3: 0,  # NestedPrivate      -> NotPublic
    5: 0,  # NestedAssembly     -> NotPublic
    6: 0,  # NestedFamANDAssem  -> NotPublic
}


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    dry_run = "--dry-run" in sys.argv

    m = Meta(path)
    nested_rids = set()
    for i in range(m.rows.get("NestedClass", 0)):
        nested_rids.add(m.read_row("NestedClass", i)["NestedClass"])

    # TypeDef row layout: Flags(u4) Name(s) Namespace(s) Extends(c) FieldList(t) MethodList(t)
    row_size = sum(m.col_sizes["TypeDef"])
    # Meta.table_off already holds absolute file offsets (it is derived from the
    # file offset of the #~ stream), so no metadata base needs adding.
    table_file_base = m.table_off["TypeDef"]

    broken = []
    for index in range(1, m.rows["TypeDef"]):
        rid = index + 1
        row = m.read_row("TypeDef", index)
        visibility = row["Flags"] & 0x7
        if visibility in (0, 1):
            if rid in nested_rids:
                broken.append((rid, index, row, "toplevel-visible but present in NestedClass"))
            continue
        if rid in nested_rids:
            continue  # legitimately nested
        broken.append((rid, index, row, "nested visibility but absent from NestedClass"))

    print("assembly     : %s" % path)
    print("TypeDef rows : %d   NestedClass rows: %d"
          % (m.rows["TypeDef"], m.rows.get("NestedClass", 0)))
    print("anomalies    : %d" % len(broken))

    if not broken:
        print("\nnothing to do")
        return 0

    patches = []
    for rid, index, row, why in broken:
        name = (m.get_string(row["Name"]) or b"?").decode("utf-8", "replace")
        ns = (m.get_string(row["Namespace"]) or b"?").decode("utf-8", "replace")
        visibility = row["Flags"] & 0x7
        new_flags = row["Flags"]
        if visibility in FIX:
            new_flags = (row["Flags"] & ~0x7) | FIX[visibility]
        file_offset = table_file_base + row_size * index
        print("  RID %-6d token 0x%08X  %s%s" % (rid, 0x02000000 | rid, ns + "." if ns else "", name))
        print("      %s: %s -> %s   flags 0x%08X -> 0x%08X   file offset 0x%X"
              % (why, VISIBILITY.get(visibility, "?"),
                 VISIBILITY.get(new_flags & 0x7, "?"), row["Flags"], new_flags, file_offset))
        if new_flags != row["Flags"]:
            patches.append((rid, file_offset, row["Flags"], new_flags))

    if not patches:
        print("\nnothing patchable (all anomalies are structural, not flag-only)")
        return 1

    if dry_run:
        print("\n--dry-run: %d patch(es) not applied" % len(patches))
        return 0

    backup = path + ".orig.bak"
    if not os.path.exists(backup):
        shutil.copy2(path, backup)
        print("\nbackup       : %s" % backup)
    else:
        print("\nbackup       : %s (already present)" % backup)

    with open(path, "r+b") as f:
        for rid, offset, old, new in patches:
            f.seek(offset)
            current = struct.unpack("<I", f.read(4))[0]
            if current != old:
                print("  !! RID %d: expected 0x%08X at 0x%X but found 0x%08X - skipping"
                      % (rid, old, offset, current))
                continue
            f.seek(offset)
            f.write(struct.pack("<I", new))
            print("  patched RID %d at 0x%X: 0x%08X -> 0x%08X" % (rid, offset, old, new))

    print("\nverifying...")
    m2 = Meta(path)
    nested2 = set()
    for i in range(m2.rows.get("NestedClass", 0)):
        nested2.add(m2.read_row("NestedClass", i)["NestedClass"])
    remaining = 0
    for index in range(1, m2.rows["TypeDef"]):
        rid = index + 1
        visibility = m2.read_row("TypeDef", index)["Flags"] & 0x7
        if visibility in (0, 1) and rid in nested2:
            remaining += 1
        elif visibility not in (0, 1) and rid not in nested2:
            remaining += 1
    print("remaining anomalies: %d" % remaining)
    return 0 if remaining == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
