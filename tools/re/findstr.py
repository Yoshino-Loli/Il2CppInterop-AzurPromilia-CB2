#!/usr/bin/env python
"""Find a byte string in a PE image and locate all RIP-relative code xrefs to it.

Usage:
  findstr.py <pe> <substring> [--disasm N] [--range 0xN]
"""
import sys, re, struct
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64


def find_string(pe, needle):
    data = pe.__data__
    hits = []
    start = 0
    nb = needle.encode() if isinstance(needle, str) else needle
    while True:
        i = data.find(nb, start)
        if i < 0:
            break
        hits.append(i)
        start = i + 1
    return hits


def section_of(pe, off):
    for s in pe.sections:
        if s.PointerToRawData <= off < s.PointerToRawData + s.SizeOfRawData:
            return s
    return None


def find_xrefs(pe, target_va, text_sections):
    """Scan .text for `lea reg, [rip+disp32]` / `mov reg, [rip+disp32]` targeting target_va."""
    out = []
    for s in text_sections:
        data = s.get_data()
        base_va = pe.OPTIONAL_HEADER.ImageBase + s.VirtualAddress
        # quick prefilter: look for the 4-byte displacement value
        for i in range(0, len(data) - 7):
            # 48 8D 05/0D/15/1D/25/2D/35/3D  disp32  (lea r64, [rip+disp])
            if data[i] == 0x48 and data[i + 1] == 0x8D and (data[i + 2] & 0xC7) == 0x05:
                disp = struct.unpack_from('<i', data, i + 3)[0]
                insn_va = base_va + i
                if insn_va + 7 + disp == target_va:
                    out.append(insn_va)
            # 4C 8D 05..  lea r8-r15
            elif data[i] == 0x4C and data[i + 1] == 0x8D and (data[i + 2] & 0xC7) == 0x05:
                disp = struct.unpack_from('<i', data, i + 3)[0]
                insn_va = base_va + i
                if insn_va + 7 + disp == target_va:
                    out.append(insn_va)
    return out


def main():
    path = sys.argv[1]
    needle = sys.argv[2]
    disasm_after = 0
    back = 0x200
    if '--disasm' in sys.argv:
        disasm_after = int(sys.argv[sys.argv.index('--disasm') + 1], 0)
    if '--back' in sys.argv:
        back = int(sys.argv[sys.argv.index('--back') + 1], 0)

    pe = pefile.PE(path, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    print(f"ImageBase = 0x{base:X}")

    hits = find_string(pe, needle)
    print(f"string hits: {len(hits)}")
    for off in hits[:10]:
        s = section_of(pe, off)
        if not s:
            continue
        va = base + s.VirtualAddress + (off - s.PointerToRawData)
        print(f"  file_off=0x{off:X} section={s.Name.decode().strip(chr(0))} va=0x{va:X}")

    text = [s for s in pe.sections if s.Name.rstrip(b'\0') in (b'.text', b'.text$mn')]
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = False

    for off in hits[:10]:
        s = section_of(pe, off)
        if not s:
            continue
        va = base + s.VirtualAddress + (off - s.PointerToRawData)
        xrefs = find_xrefs(pe, va, text)
        print(f"\n=== xrefs to 0x{va:X}: {len(xrefs)} ===")
        for xva in xrefs[:6]:
            rva = xva - base
            sec = None
            for ts in text:
                if ts.VirtualAddress <= rva < ts.VirtualAddress + ts.Misc_VirtualSize:
                    sec = ts
                    break
            if not sec:
                continue
            foff = sec.PointerToRawData + (rva - sec.VirtualAddress)
            start = max(sec.PointerToRawData, foff - back)
            code = pe.__data__[start:foff + disasm_after]
            code_va = base + sec.VirtualAddress + (start - sec.PointerToRawData)
            print(f"--- xref at 0x{xva:X} (rva 0x{rva:X}) ---")
            for insn in md.disasm(code, code_va):
                mark = '  <<<<' if insn.address == xva else ''
                print(f"  0x{insn.address:X}  {insn.mnemonic:<8}{insn.op_str}{mark}")


if __name__ == '__main__':
    main()
