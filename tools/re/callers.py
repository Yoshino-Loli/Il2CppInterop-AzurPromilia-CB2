#!/usr/bin/env python
"""Find direct `call rel32` / `jmp rel32` references to a target RVA.

Usage: callers.py <pe> <rva-or-va> [--limit N]
"""
import sys, struct
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64


def main():
    path = sys.argv[1]
    val = int(sys.argv[2], 0)
    limit = int(sys.argv[sys.argv.index('--limit') + 1]) if '--limit' in sys.argv else 40
    pe = pefile.PE(path, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    rva = val - base if val >= base else val
    target = base + rva
    text = [s for s in pe.sections if s.Name.rstrip(b'\0') == b'.text'][0]
    data = text.get_data()
    bva = base + text.VirtualAddress

    hits = []
    for op in (b'\xe8', b'\xe9'):
        st = 0
        while True:
            i = data.find(op, st)
            if i < 0:
                break
            st = i + 1
            disp = struct.unpack_from('<i', data, i + 1)[0]
            iva = bva + i
            if iva + 5 + disp == target:
                hits.append((iva, 'call' if op == b'\xe8' else 'jmp'))

    # also indirect: mov [rip+x], imm64 / lea rdx,[rip+...] referencing a pointer slot
    hits.sort()
    print(f"target 0x{target:X} (rva 0x{rva:X}) : {len(hits)} direct refs")
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    for iva, kind in hits[:limit]:
        r = iva - base
        foff = text.PointerToRawData + (r - text.VirtualAddress)
        start = max(text.PointerToRawData, foff - 0x30)
        code = pe.__data__[start:foff + 5]
        cva = base + text.VirtualAddress + (start - text.PointerToRawData)
        print(f"\n--- {kind} at 0x{iva:X} ---")
        for insn in md.disasm(bytes(code), cva):
            m = '  <<<<' if insn.address == iva else ''
            print(f"  0x{insn.address:X}  {insn.mnemonic:<9}{insn.op_str}{m}")


if __name__ == '__main__':
    main()
