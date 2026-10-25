#!/usr/bin/env python3
"""CLI (.NET) metadata integrity checker.

Parses the ECMA-335 metadata tables of a managed assembly and validates:
  * PE / CLI header sanity
  * metadata stream layout
  * per-table row sizes vs. actual stream length (truncation detection)
  * heap index bounds (#Strings / #Blob / #GUID / #US)
  * TypeDef name sanity (garbage / duplicates / empty)
  * TypeDef.Extends coded-index validity

Usage:  python mdcheck.py <assembly.dll> [--types] [--limit N]
"""
import struct
import sys
import collections
import pefile

# ---------------------------------------------------------------- table schema
# column kinds:
#   u1/u2/u4          fixed width
#   s / g / b         heap index (#Strings / #GUID / #Blob)
#   t:NAME            simple table index
#   c:NAME            coded index
TABLES = [
    ("Module", [("Generation", "u2"), ("Name", "s"), ("Mvid", "g"), ("EncId", "g"), ("EncBaseId", "g")]),
    ("TypeRef", [("ResolutionScope", "c:ResolutionScope"), ("Name", "s"), ("Namespace", "s")]),
    ("TypeDef", [("Flags", "u4"), ("Name", "s"), ("Namespace", "s"),
                 ("Extends", "c:TypeDefOrRef"), ("FieldList", "t:Field"), ("MethodList", "t:MethodDef")]),
    ("FieldPtr", [("Field", "t:Field")]),
    ("Field", [("Flags", "u2"), ("Name", "s"), ("Signature", "b")]),
    ("MethodPtr", [("Method", "t:MethodDef")]),
    ("MethodDef", [("RVA", "u4"), ("ImplFlags", "u2"), ("Flags", "u2"),
                   ("Name", "s"), ("Signature", "b"), ("ParamList", "t:Param")]),
    ("ParamPtr", [("Param", "t:Param")]),
    ("Param", [("Flags", "u2"), ("Sequence", "u2"), ("Name", "s")]),
    ("InterfaceImpl", [("Class", "t:TypeDef"), ("Interface", "c:TypeDefOrRef")]),
    ("MemberRef", [("Class", "c:MemberRefParent"), ("Name", "s"), ("Signature", "b")]),
    ("Constant", [("Type", "u1"), ("Padding", "u1"), ("Parent", "c:HasConstant"), ("Value", "b")]),
    ("CustomAttribute", [("Parent", "c:HasCustomAttribute"), ("Type", "c:CustomAttributeType"), ("Value", "b")]),
    ("FieldMarshal", [("Parent", "c:HasFieldMarshal"), ("NativeType", "b")]),
    ("DeclSecurity", [("Action", "u2"), ("Parent", "c:HasDeclSecurity"), ("PermissionSet", "b")]),
    ("ClassLayout", [("PackingSize", "u2"), ("ClassSize", "u4"), ("Parent", "t:TypeDef")]),
    ("FieldLayout", [("Offset", "u4"), ("Field", "t:Field")]),
    ("StandAloneSig", [("Signature", "b")]),
    ("EventMap", [("Parent", "t:TypeDef"), ("EventList", "t:Event")]),
    ("EventPtr", [("Event", "t:Event")]),
    ("Event", [("EventFlags", "u2"), ("Name", "s"), ("EventType", "c:TypeDefOrRef")]),
    ("PropertyMap", [("Parent", "t:TypeDef"), ("PropertyList", "t:Property")]),
    ("PropertyPtr", [("Property", "t:Property")]),
    ("Property", [("Flags", "u2"), ("Name", "s"), ("Type", "b")]),
    ("MethodSemantics", [("Semantics", "u2"), ("Method", "t:MethodDef"), ("Association", "c:HasSemantics")]),
    ("MethodImpl", [("Class", "t:TypeDef"), ("MethodBody", "c:MethodDefOrRef"),
                    ("MethodDeclaration", "c:MethodDefOrRef")]),
    ("ModuleRef", [("Name", "s")]),
    ("TypeSpec", [("Signature", "b")]),
    ("ImplMap", [("MappingFlags", "u2"), ("MemberForwarded", "c:MemberForwarded"),
                 ("ImportName", "s"), ("ImportScope", "t:ModuleRef")]),
    ("FieldRVA", [("RVA", "u4"), ("Field", "t:Field")]),
    ("ENCLog", [("Token", "u4"), ("FuncCode", "u4")]),
    ("ENCMap", [("Token", "u4")]),
    ("Assembly", [("HashAlgId", "u4"), ("MajorVersion", "u2"), ("MinorVersion", "u2"),
                  ("BuildNumber", "u2"), ("RevisionNumber", "u2"), ("Flags", "u4"),
                  ("PublicKey", "b"), ("Name", "s"), ("Culture", "s")]),
    ("AssemblyProcessor", [("Processor", "u4")]),
    ("AssemblyOS", [("OSPlatformID", "u4"), ("OSMajorVersion", "u4"), ("OSMinorVersion", "u4")]),
    ("AssemblyRef", [("MajorVersion", "u2"), ("MinorVersion", "u2"), ("BuildNumber", "u2"),
                     ("RevisionNumber", "u2"), ("Flags", "u4"), ("PublicKeyOrToken", "b"),
                     ("Name", "s"), ("Culture", "s"), ("HashValue", "b")]),
    ("AssemblyRefProcessor", [("Processor", "u4"), ("AssemblyRef", "t:AssemblyRef")]),
    ("AssemblyRefOS", [("OSPlatformId", "u4"), ("OSMajorVersion", "u4"),
                       ("OSMinorVersion", "u4"), ("AssemblyRef", "t:AssemblyRef")]),
    ("File", [("Flags", "u4"), ("Name", "s"), ("HashValue", "b")]),
    ("ExportedType", [("Flags", "u4"), ("TypeDefId", "u4"), ("TypeName", "s"),
                      ("TypeNamespace", "s"), ("Implementation", "c:Implementation")]),
    ("ManifestResource", [("Offset", "u4"), ("Flags", "u4"), ("Name", "s"),
                          ("Implementation", "c:Implementation")]),
    ("NestedClass", [("NestedClass", "t:TypeDef"), ("EnclosingClass", "t:TypeDef")]),
    ("GenericParam", [("Number", "u2"), ("Flags", "u2"), ("Owner", "c:TypeOrMethodDef"), ("Name", "s")]),
    ("MethodSpec", [("Method", "c:MethodDefOrRef"), ("Instantiation", "b")]),
    ("GenericParamConstraint", [("Owner", "t:GenericParam"), ("Constraint", "c:TypeDefOrRef")]),
]

TABLE_ID = {name: i for i, (name, _) in enumerate(TABLES)}

# name -> (list of (table name, tag value), tag bits)
CODED = {
    "TypeDefOrRef": ([("TypeDef", 0), ("TypeRef", 1), ("TypeSpec", 2)], 2),
    "HasConstant": ([("Field", 0), ("Param", 1), ("Property", 2)], 2),
    "HasCustomAttribute": ([("MethodDef", 0), ("Field", 1), ("TypeRef", 2), ("TypeDef", 3),
                            ("Param", 4), ("InterfaceImpl", 5), ("MemberRef", 6), ("Module", 7),
                            ("DeclSecurity", 8), ("Property", 9), ("Event", 10), ("StandAloneSig", 11),
                            ("ModuleRef", 12), ("TypeSpec", 13), ("Assembly", 14), ("AssemblyRef", 15),
                            ("File", 16), ("ExportedType", 17), ("ManifestResource", 18),
                            ("GenericParam", 19), ("GenericParamConstraint", 20), ("MethodSpec", 21)], 5),
    "HasFieldMarshal": ([("Field", 0), ("Param", 1)], 1),
    "HasDeclSecurity": ([("TypeDef", 0), ("MethodDef", 1), ("Assembly", 2)], 2),
    "MemberRefParent": ([("TypeDef", 0), ("TypeRef", 1), ("ModuleRef", 2), ("MethodDef", 3),
                         ("TypeSpec", 4)], 3),
    "HasSemantics": ([("Event", 0), ("Property", 1)], 1),
    "MethodDefOrRef": ([("MethodDef", 0), ("MemberRef", 1)], 1),
    "MemberForwarded": ([("Field", 0), ("MethodDef", 1)], 1),
    "Implementation": ([("File", 0), ("AssemblyRef", 1), ("ExportedType", 2)], 2),
    "CustomAttributeType": ([("MethodDef", 2), ("MemberRef", 3)], 3),
    "ResolutionScope": ([("Module", 0), ("ModuleRef", 1), ("AssemblyRef", 2), ("TypeRef", 3)], 2),
    "TypeOrMethodDef": ([("TypeDef", 0), ("MethodDef", 1)], 1),
}


class Meta:
    def __init__(self, path):
        self.path = path
        self.warnings = []
        self.errors = []
        with open(path, "rb") as f:
            self.data = f.read()
        self.pe = pefile.PE(data=self.data, fast_load=True)
        self._read_headers()
        self._read_cli()
        self._read_meta_root()
        self._read_tables()

    # -------------------------------------------------------------- PE
    def rva_to_off(self, rva):
        for s in self.pe.sections:
            va = s.VirtualAddress
            size = max(s.Misc_VirtualSize, s.SizeOfRawData)
            if va <= rva < va + size:
                delta = rva - va
                if delta >= s.SizeOfRawData:
                    raise ValueError("RVA 0x%X lands in uninitialized tail of %s "
                                     "(delta 0x%X >= RawSize 0x%X)"
                                     % (rva, s.Name.rstrip(b"\0").decode("latin1"),
                                        delta, s.SizeOfRawData))
                return s.PointerToRawData + delta
        raise ValueError("RVA 0x%X not inside any section" % rva)

    def _read_headers(self):
        pe = self.pe
        self.machine = pe.FILE_HEADER.Machine
        opt = pe.OPTIONAL_HEADER
        self.is_pe32plus = (opt.Magic == 0x20B)
        self.image_base = opt.ImageBase
        dirs = opt.DATA_DIRECTORY
        if len(dirs) <= 14:
            raise ValueError("no CLI data directory")
        d = dirs[14]
        self.cli_rva, self.cli_size = d.VirtualAddress, d.Size
        self.section_count = pe.FILE_HEADER.NumberOfSections

    def _read_cli(self):
        if self.cli_rva == 0:
            raise ValueError("assembly has no CLI header")
        off = self.rva_to_off(self.cli_rva)
        (cb, maj, mnr, md_rva, md_size, flags, entry) = struct.unpack_from("<IHHIIII", self.data, off)
        self.cli = dict(cb=cb, runtime="%d.%d" % (maj, mnr), flags=flags, entry=entry)
        self.md_rva, self.md_size = md_rva, md_size
        # strong name signature
        sn_rva, sn_size = struct.unpack_from("<II", self.data, off + 32)
        self.strongname = (sn_rva, sn_size)
        # vtable fixups
        vtf_rva, vtf_size = struct.unpack_from("<II", self.data, off + 40)
        self.vtablefixups = (vtf_rva, vtf_size)

    def _read_meta_root(self):
        off = self.rva_to_off(self.md_rva)
        sig, maj, mnr, resv, vlen = struct.unpack_from("<IHHII", self.data, off)
        if sig != 0x424A5342:
            raise ValueError("bad metadata signature 0x%08X" % sig)
        self.md_version = self.data[off + 16:off + 16 + vlen].split(b"\0")[0].decode("latin1")
        p = off + 16 + vlen
        flags, nstreams = struct.unpack_from("<HH", self.data, p)
        p += 4
        self.streams = {}
        for _ in range(nstreams):
            s_off, s_size = struct.unpack_from("<II", self.data, p)
            p += 8
            name_end = self.data.index(b"\0", p)
            name = self.data[p:name_end].decode("latin1")
            p = (name_end + 4) & ~3
            self.streams[name] = (s_off, s_size)
        # bounds check
        for name, (s_off, s_size) in self.streams.items():
            if s_off + s_size > self.md_size:
                self.errors.append("stream %s (off 0x%X size 0x%X) overflows metadata size 0x%X"
                                   % (name, s_off, s_size, self.md_size))

    # -------------------------------------------------------------- tables
    def _read_tables(self):
        key = "#~" if "#~" in self.streams else "#-"
        if key not in self.streams:
            raise ValueError("no #~ stream")
        self.tables_key = key
        s_off, s_size = self.streams[key]
        base = self.rva_to_off(self.md_rva) + s_off
        self.tables_base = base
        self.tables_size = s_size
        p = base
        resv, maj, mnr, heap_sizes, resv2 = struct.unpack_from("<IBBBB", self.data, p)
        p += 8
        valid, sorted_ = struct.unpack_from("<QQ", self.data, p)
        p += 16
        self.heap_sizes = heap_sizes
        self.string_idx = 4 if heap_sizes & 0x01 else 2
        self.guid_idx = 4 if heap_sizes & 0x02 else 2
        self.blob_idx = 4 if heap_sizes & 0x04 else 2

        self.rows = {}
        for t in range(64):
            if valid >> t & 1:
                if t >= len(TABLES):
                    self.errors.append("unknown metadata table id %d present" % t)
                    continue
                (n,) = struct.unpack_from("<I", self.data, p)
                p += 4
                self.rows[TABLES[t][0]] = n

        # column sizes need row counts of *all* tables -> compute after we know them
        self.col_sizes = {}
        for name, cols in TABLES:
            sizes = []
            for cname, kind in cols:
                sizes.append(self._col_size(kind))
            self.col_sizes[name] = sizes

        # layout
        self.table_off = {}
        cur = p
        self.layout = []
        for name, cols in TABLES:
            n = self.rows.get(name, 0)
            if n == 0:
                self.table_off[name] = cur
                self.layout.append((name, 0, 0))
                continue
            row_size = sum(self.col_sizes[name])
            need = row_size * n
            self.table_off[name] = cur
            self.layout.append((name, cur, row_size))
            cur += need
        self.tables_end = cur
        self.stream_end = base + s_size
        if cur > self.stream_end:
            self.errors.append("metadata tables overflow #~ stream: need 0x%X, stream ends 0x%X "
                               "(overrun %d bytes)" % (cur, self.stream_end, cur - self.stream_end))
        elif cur != self.stream_end:
            self.warnings.append("#~ stream has %d trailing byte(s) after last table"
                                 % (self.stream_end - cur))

        self._check_heaps()

    def _col_size(self, kind):
        if kind == "u1":
            return 1
        if kind == "u2":
            return 2
        if kind == "u4":
            return 4
        if kind == "s":
            return self.string_idx
        if kind == "g":
            return self.guid_idx
        if kind == "b":
            return self.blob_idx
        if kind.startswith("t:"):
            tname = kind[2:]
            return 4 if self.rows.get(tname, 0) > 0xFFFF else 2
        if kind.startswith("c:"):
            cname = kind[2:]
            entries, bits = CODED[cname]
            mx = max(self.rows.get(t, 0) for t, _ in entries)
            return 4 if mx >= (1 << (16 - bits)) else 2
        raise ValueError("bad column kind %r" % kind)

    def _check_heaps(self):
        for heap, size in (("#Strings", self.string_idx), ("#Blob", self.blob_idx), ("#GUID", self.guid_idx)):
            if heap not in self.streams:
                continue
            off, ln = self.streams[heap]
            if size == 2 and ln > 0xFFFF:
                self.errors.append("%s is 0x%X bytes but indexes are 2-byte" % (heap, ln))
        # #Strings must be NUL terminated
        if "#Strings" in self.streams:
            off, ln = self.streams["#Strings"]
            if ln and self.data[self.rva_to_off(self.md_rva) + off + ln - 1] != 0:
                self.errors.append("#Strings heap is not NUL-terminated at its end")

    # -------------------------------------------------------------- rows
    def read_row(self, table, idx):
        """idx is 0-based."""
        cols = dict(TABLES)[table]
        sizes = self.col_sizes[table]
        p = self.table_off[table] + sum(sizes) * idx
        out = {}
        for (cname, kind), size in zip(cols, sizes):
            v = int.from_bytes(self.data[p:p + size], "little")
            out[cname] = v
            p += size
        return out

    def get_string(self, idx):
        off, ln = self.streams["#Strings"]
        base = self.rva_to_off(self.md_rva) + off
        if idx >= ln:
            return None
        end = self.data.find(b"\0", base + idx, base + ln)
        if end < 0:
            return None
        return self.data[base + idx:end]

    def decode_coded(self, kind, value):
        entries, bits = CODED[kind]
        tag = value & ((1 << bits) - 1)
        row = value >> bits
        for tname, ttag in entries:
            if ttag == tag:
                return tname, row
        return None, row


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    path = sys.argv[1]
    show_types = "--types" in sys.argv
    limit = 40
    if "--limit" in sys.argv:
        limit = int(sys.argv[sys.argv.index("--limit") + 1])

    print("== %s ==" % path)
    try:
        m = Meta(path)
    except Exception as e:
        print("FATAL: %s" % e)
        return 1

    print("size            : %d bytes" % len(m.data))
    print("machine         : 0x%X (%s)" % (m.machine, "x64" if m.machine == 0x8664 else "x86/other"))
    print("sections        : %d" % m.section_count)
    print("image base      : 0x%X" % m.image_base)
    print("CLI header      : rva 0x%X size 0x%X  runtime %s  flags 0x%X"
          % (m.cli_rva, m.cli_size, m.cli["runtime"], m.cli["flags"]))
    print("strong name     : rva 0x%X size 0x%X" % m.strongname)
    print("vtable fixups   : rva 0x%X size 0x%X" % m.vtablefixups)
    print("metadata        : rva 0x%X size 0x%X  version %r" % (m.md_rva, m.md_size, m.md_version))
    print("heap sizes byte : 0x%02X (str=%d blob=%d guid=%d)"
          % (m.heap_sizes, m.string_idx, m.blob_idx, m.guid_idx))
    print("streams:")
    for name, (off, ln) in sorted(m.streams.items(), key=lambda kv: kv[1][0]):
        print("   %-10s off 0x%-7X size 0x%-7X" % (name, off, ln))
    print("tables (%s):" % m.tables_key)
    for name, off, row_size in m.layout:
        n = m.rows.get(name, 0)
        if n:
            print("   %-26s rows %-8d row_size %-3d off 0x%X" % (name, n, row_size, off))
    print("tables end      : 0x%X   #~ stream end: 0x%X" % (m.tables_end, m.stream_end))

    if m.errors:
        print("\nERRORS:")
        for e in m.errors:
            print("  !! %s" % e)
    if m.warnings:
        print("\nWARNINGS:")
        for w in m.warnings:
            print("  ~  %s" % w)
    if not m.errors and not m.warnings:
        print("\nstructure: OK")

    # ---- TypeDef sanity
    ntd = m.rows.get("TypeDef", 0)
    print("\nTypeDef rows: %d" % ntd)
    seen = {}
    bad = []
    dup = []
    for i in range(ntd):
        r = m.read_row("TypeDef", i)
        nm = m.get_string(r["Name"])
        ns = m.get_string(r["Namespace"])
        if nm is None:
            bad.append((i, "Name string index 0x%X out of range" % r["Name"]))
            continue
        if ns is None:
            bad.append((i, "Namespace string index 0x%X out of range" % r["Namespace"]))
            continue
        try:
            nm_s = nm.decode("utf-8")
        except UnicodeDecodeError:
            bad.append((i, "Name is not valid UTF-8: %r" % nm))
            continue
        if i == 0:
            continue  # <Module>
        if nm_s == "":
            bad.append((i, "empty type name"))
        key = (ns.decode("utf-8", "replace"), nm_s)
        if key in seen:
            dup.append((i, seen[key], key))
        else:
            seen[key] = i
        ext_t, ext_i = m.decode_coded("TypeDefOrRef", r["Extends"])
        if r["Extends"] != 0:
            if ext_t is None:
                bad.append((i, "Extends has invalid tag 0x%X" % (r["Extends"] & 3)))
            elif ext_i >= m.rows.get(ext_t, 0):
                bad.append((i, "Extends -> %s row %d out of range (max %d)"
                            % (ext_t, ext_i, m.rows.get(ext_t, 0))))

    if bad:
        print("BAD TypeDefs: %d" % len(bad))
        for i, msg in bad[:limit]:
            print("   [%d] %s" % (i, msg))
        if len(bad) > limit:
            print("   ... %d more" % (len(bad) - limit))
    else:
        print("TypeDef names/extents: OK")

    if dup:
        print("DUPLICATE types: %d" % len(dup))
        for i, j, key in dup[:limit]:
            print("   [%d] duplicates [%d]  %s::%s" % (i, j, key[0], key[1]))
    else:
        print("duplicate types: none")

    if show_types:
        print("\n--- first %d types ---" % limit)
        for i in range(min(ntd, limit)):
            r = m.read_row("TypeDef", i)
            nm = (m.get_string(r["Name"]) or b"?").decode("utf-8", "replace")
            ns = (m.get_string(r["Namespace"]) or b"?").decode("utf-8", "replace")
            ext_t, ext_i = m.decode_coded("TypeDefOrRef", r["Extends"])
            print("   [%4d] 0x%08X %s%s  : %s[%d]" % (i, r["Flags"], ns, nm, ext_t, ext_i))

    return 0 if not m.errors and not bad else 1


if __name__ == "__main__":
    sys.exit(main())
