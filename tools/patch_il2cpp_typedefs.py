#!/usr/bin/env python
"""Fix TypeDef flags that the CLR rejects in Il2CppInterop's generated assemblies.

Two independent defects show up in the generated metadata. Both make Assembly.GetTypes()
throw for the whole assembly, and in a game process that has Il2CppInterop's detours installed
the throw escalates into an uncatchable failfast, which is what kills the process before
UniverseLib can finish building its type cache.

1. TypeAttributes.Import (0x1000) on a type that is not an interface.
   Il2CppInterop emits IL2CPP interfaces as plain classes deriving from
   Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, and a few of them also carry the
   "imported from a COM type library" bit. The CLR answers with
   "cannot extend from any other type".
   Seen in Il2Cppmscorlib: System.Runtime.InteropServices.ComTypes.ITypeInfo / ITypeLib.

2. TypeAttributes.WindowsRuntime (0x4000).
   Il2Cpp.IActivationFactory in Il2Cpp__Generated is declared as a WinRT type inside an
   ordinary managed assembly, which fails with a bare "Failure has occurred while loading a
   type". The flag is a hint nothing in managed code can act on.

Usage:
  patch_il2cpp_typedefs.py <dir-with-assemblies> [--dry-run]
  patch_il2cpp_typedefs.py <assembly.dll> [--dry-run]
"""
import glob
import os
import shutil
import sys

from mdcheck import Meta

IMPORT_BIT = 0x00001000
INTERFACE_BIT = 0x00000020
WINDOWS_RUNTIME_BIT = 0x00004000


def plan(path):
    """Return [(index, namespace, name, flags, new_flags)] for one assembly."""
    meta = Meta(path)
    if 'TypeDef' not in meta.rows:
        return meta, []

    fixes = []
    for index in range(meta.rows['TypeDef']):
        row = meta.read_row('TypeDef', index)
        flags = row['Flags']
        new_flags = flags

        if flags & IMPORT_BIT and not flags & INTERFACE_BIT:
            new_flags &= ~IMPORT_BIT
        if flags & WINDOWS_RUNTIME_BIT:
            new_flags &= ~WINDOWS_RUNTIME_BIT

        if new_flags == flags:
            continue

        name = meta.get_string(row['Name'])
        namespaze = meta.get_string(row['Namespace'])
        if isinstance(name, bytes):
            name = name.decode('utf-8', 'replace')
        if isinstance(namespaze, bytes):
            namespaze = namespaze.decode('utf-8', 'replace')

        fixes.append((index, namespaze, name, flags, new_flags))

    return meta, fixes


def apply(path, dry_run):
    try:
        meta, fixes = plan(path)
    except Exception as ex:
        print(f'{os.path.basename(path)}: SKIP ({ex})')
        return 0

    if not fixes:
        return 0

    label = os.path.basename(path)
    for _, namespaze, name, flags, new_flags in fixes:
        print(f'  {label}: {namespaze}.{name} 0x{flags:08X} -> 0x{new_flags:08X}')

    if dry_run:
        return len(fixes)

    shutil.copy2(path, path + '.orig.bak')

    row_size = sum(meta.col_sizes['TypeDef'])
    table_base = meta.table_off['TypeDef']
    with open(path, 'r+b') as handle:
        for index, _, _, _, new_flags in fixes:
            handle.seek(table_base + row_size * index)  # Flags is the first column
            handle.write(new_flags.to_bytes(4, 'little'))

    # Re-read to confirm the file on disk is clean.
    _, remaining = plan(path)
    if remaining:
        print(f'  {label}: WARNING {len(remaining)} fix(es) did not stick')

    return len(fixes)


def main():
    target = sys.argv[1]
    dry_run = '--dry-run' in sys.argv

    if os.path.isdir(target):
        files = sorted(glob.glob(os.path.join(target, '*.dll')))
    else:
        files = [target]

    print(f'scanning {len(files)} assembly(ies)')
    total = 0
    touched = 0
    for path in files:
        before = total
        total += apply(path, dry_run)
        if total != before:
            touched += 1

    print(f'{total} TypeDef(s) in {touched} assembly(ies) {"would be" if dry_run else ""} patched')
    return 0


if __name__ == '__main__':
    sys.exit(main())
