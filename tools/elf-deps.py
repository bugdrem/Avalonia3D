import struct
import sys


def dt_needed(path):
    with open(path, "rb") as f:
        data = f.read()

    if data[:4] != b"\x7fELF":
        return None, "not ELF"
    is64 = data[4] == 2
    little = data[5] == 1

    # 64-bit ELF header
    e_phoff = struct.unpack_from("<Q", data, 0x20)[0]
    e_phentsize = struct.unpack_from("<H", data, 0x36)[0]
    e_phnum = struct.unpack_from("<H", data, 0x38)[0]

    needed = []
    for i in range(e_phnum):
        off = e_phoff + i * e_phentsize
        p_type = struct.unpack_from("<I", data, off)[0]
        p_offset = struct.unpack_from("<Q", data, off + 0x08)[0]
        p_filesz = struct.unpack_from("<Q", data, off + 0x20)[0]
        if p_type != 2:  # PT_DYNAMIC
            continue
        dyn = data[p_offset : p_offset + p_filesz]
        pos = 0
        while pos + 16 <= len(dyn):
            d_tag = struct.unpack_from("<q", dyn, pos)[0]
            d_val = struct.unpack_from("<Q", dyn, pos + 8)[0]
            pos += 16
            if d_tag == 1:  # DT_NEEDED
                needed.append(d_val)
            if d_tag == 0:  # DT_NULL
                break
        # 读取字符串表（DT_STRTAB 虚拟地址近似用文件偏移，本文件中 .dynstr 与 PT_DYNAMIC 的偏移关系简化处理）
        break

    # 简化：直接在文件里找 DT_STRTAB
    # 重新扫描 PT_DYNAMIC 找 DT_STRTAB
    strtab_vaddr = None
    for i in range(e_phnum):
        off = e_phoff + i * e_phentsize
        p_type = struct.unpack_from("<I", data, off)[0]
        p_offset = struct.unpack_from("<Q", data, off + 0x08)[0]
        p_vaddr = struct.unpack_from("<Q", data, off + 0x10)[0]
        p_filesz = struct.unpack_from("<Q", data, off + 0x20)[0]
        if p_type == 2:
            dyn = data[p_offset : p_offset + p_filesz]
            pos = 0
            while pos + 16 <= len(dyn):
                d_tag = struct.unpack_from("<q", dyn, pos)[0]
                d_val = struct.unpack_from("<Q", dyn, pos + 8)[0]
                pos += 16
                if d_tag == 5:  # DT_STRTAB
                    strtab_vaddr = d_val
                if d_tag == 0:
                    break
            # 近似：假定 vaddr==offset（常见于第一加载段）
            strtab_off = strtab_vaddr
            names = []
            for v in needed:
                end = data.index(b"\x00", strtab_off + v)
                names.append(data[strtab_off + v : end].decode("utf-8", "replace"))
            return names, None
    return needed, "no dynstr"


files = [
    "artifacts/sc/linux-x64/Avalonia3DViewer",
    "artifacts/sc/linux-x64/libcoreclr.so",
    "artifacts/sc/linux-x64/libSkiaSharp.so",
    "artifacts/sc/linux-x64/libSystem.Native.so",
    "artifacts/sc/linux-x64/libSystem.Security.Cryptography.Native.OpenSsl.so",
]

all_needed = set()
for f in files:
    try:
        names, err = dt_needed(f)
        if names is None:
            print(f"{f}: ERR {err}")
            continue
        print(f"{f}:")
        for n in names:
            print(f"    {n}")
            all_needed.add(n)
    except Exception as e:
        print(f"{f}: EXC {e}")

print()
print("=== union of external dependencies ===")
for n in sorted(all_needed):
    print(" ", n)
