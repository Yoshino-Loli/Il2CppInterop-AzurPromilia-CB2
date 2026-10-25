#!/usr/bin/env python
"""Dump the exact function (from .pdata) containing a given RVA/VA.

Usage: funcat.py <pe> <va-or-rva> [--len N]
"""
import sys, struct
import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64


def parse_pdata(pe):
    base = pe.OPTIONAL_HEADER.ImageBase
    pd = [s for s in pe.sections if s.Name.rstrip(b'\0') == b'.pdata'][0]
    text = [s for s in pe.sections if s.Name.rstrip(b'\0') == b'.text'][0]
    data = pd.get_data()
    funcs = []
    for i in range(0, len(data) - 12 + 1, 12):
        begin, end, unwind = struct.unpack_from('<III', data, i)
        if begin == 0 or end == 0 or end <= begin:
            continue
        funcs.append((begin, end, unwind))
    funcs.sort()
    return funcs, text


def main():
    path = sys.argv[1]
    arg = sys.argv[2]
    val = int(arg, 0)
    pe = pefile.PE(path, fast_load=True)
    base = pe.OPTIONAL_HEADER.ImageBase
    funcs, text = parse_pdata(pe)
    rva = val - base if val >= base else val

    hit = None
    for b, e, u in funcs:
        if b <= rva < e:
            hit = (b, e, u)
            break
    if not hit:
        print(f"no .pdata function contains rva 0x{rva:X}")
        # print nearest
        near = min(funcs, key=lambda f: abs(f[0] - rva))
        print(f"nearest: 0x{near[0]:X}-0x{near[1]:X}")
        return
    b, e, u = hit
    print(f"function rva 0x{b:X} - 0x{e:X}  (va 0x{base+b:X})  size {e-b}  unwind 0x{u:X}")

    foff = text.PointerToRawData + (b - text.VirtualAddress)
    code = pe.__data__[foff:foff + (e - b)]
    md = Cs(CS_ARCH_X86, CS_MODE_64)
    md.detail = False
    for insn in md.disasm(bytes(code), base + b):
        mark = '  <<<<' if insn.address == base + rva else ''
        print(f"0x{insn.address:X}  {insn.bytes.hex():<22}{insn.mnemonic:<9}{insn.op_str}{mark}")


if __name__ == '__main__':
    main()
