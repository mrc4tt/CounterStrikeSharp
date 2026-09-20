#!/usr/bin/env python3
"""Read-only: locate CNavPhysicsInterface's vtable in a Linux CS2 libserver.so by RTTI name and
dump its slots, so the slot order assumed by src/core/cs2_sdk/interfaces/navphysicsinterface.h can
be checked after a game update. Usage: dump-navphysics-vtable.py /path/to/libserver.so [ClassName]
"""
import struct, sys

path = sys.argv[1]
cls = sys.argv[2] if len(sys.argv) > 2 else "CNavPhysicsInterface"
data = open(path, "rb").read()
assert data[:4] == b"\x7fELF" and data[4] == 2, "need a 64-bit ELF"

e_phoff, e_shoff = struct.unpack_from("<QQ", data, 0x20)
e_phentsize, e_phnum, e_shentsize, e_shnum, e_shstrndx = struct.unpack_from("<HHHHH", data, 0x36)

segs = []  # (vaddr, offset, filesz, executable)
for i in range(e_phnum):
    p_type, p_flags, p_off, p_va, _, p_fsz, _, _ = struct.unpack_from("<IIQQQQQQ", data, e_phoff + i * e_phentsize)
    if p_type == 1:
        segs.append((p_va, p_off, p_fsz, bool(p_flags & 1)))

def off2va(off):
    for va, o, sz, _ in segs:
        if o <= off < o + sz:
            return va + (off - o)

def is_exec(va):
    return any(x and v <= va < v + sz for v, _, sz, x in segs)

# Pointers in a PIC .so live in RELA relocations; the file bytes at the target are usually zero.
ptr_at = {}  # va -> pointed-to va
for i in range(e_shnum):
    sh = struct.unpack_from("<IIQQQQIIQQ", data, e_shoff + i * e_shentsize)
    if sh[1] != 4:  # SHT_RELA
        continue
    for o in range(sh[4], sh[4] + sh[5], 24):
        r_off, r_info, r_add = struct.unpack_from("<QQq", data, o)
        if (r_info & 0xFFFFFFFF) == 8:  # R_X86_64_RELATIVE
            ptr_at[r_off] = r_add

name = ("%d%s" % (len(cls), cls)).encode() + b"\0"
name_off = data.find(name)
assert name_off >= 0, "RTTI name %r not found - class renamed or removed" % name
name_va = off2va(name_off)
print("RTTI name  %s @ 0x%x" % (name[:-1].decode(), name_va))

typeinfos = [va - 8 for va, tgt in ptr_at.items() if tgt == name_va]
assert typeinfos, "no typeinfo points at the name"
for ti in typeinfos:
    print("typeinfo   @ 0x%x" % ti)
    for vt in sorted(va - 8 for va, tgt in ptr_at.items() if tgt == ti):
        print("vtable     @ 0x%x   (slot 0 at +0x10)" % vt)
        n = 0
        while True:
            fn = ptr_at.get(vt + 0x10 + n * 8)
            if fn is None or not is_exec(fn):
                break
            print("  slot %2d  -> 0x%x" % (n, fn))
            n += 1
        print("  %d slots" % n)
