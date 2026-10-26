"""将自包含发布产物打包为 tar.gz，并修复 Linux 可执行位。

背景：.NET 在 Windows 上执行 publish 时不会给 Linux apphost 设置 Unix 可执行位
（产物里是 rw-r--r--，tar 也会原样保留）。在 Ubuntu 上双击/执行会报
"没有安装可执行文件文件的查看器"。本脚本重写 tar 条目权限：
apphost 设 0755，其余 0644。

用法：
    python tools/package-linux.py [源目录] [输出.tar.gz]
默认：artifacts/sc/linux-x64 -> artifacts/sc/linux-x64.tar.gz
"""

import os
import sys
import tarfile

EXECUTABLE_NAMES = {"Avalonia3DViewer", "createdump"}


def package(src_dir: str, out_path: str) -> int:
    if not os.path.isdir(src_dir):
        print(f"源目录不存在: {src_dir}")
        return 1

    parent = os.path.dirname(os.path.abspath(src_dir))
    count = 0
    total = 0
    with tarfile.open(out_path, "w:gz") as tf:
        for root, dirs, files in os.walk(src_dir):
            dirs.sort()
            for name in sorted(files):
                path = os.path.join(root, name)
                # tar 条目名必须用 POSIX 正斜杠（Windows 的 os.path 用反斜杠，
                # 直接写进 tar 会在 Linux 解压出带反斜杠的怪文件名）
                rel = os.path.relpath(path, parent).replace(os.sep, "/")
                st = os.stat(path)
                mode = 0o755 if name in EXECUTABLE_NAMES else 0o644
                info = tarfile.TarInfo(rel)
                info.size = st.st_size
                info.mode = mode
                info.mtime = int(st.st_mtime)
                info.uname = ""
                info.gname = ""
                with open(path, "rb") as f:
                    tf.addfile(info, f)
                count += 1
                total += st.st_size
    print(f"已打包 {count} 个文件 ({total / 1024 / 1024:.1f} MB) -> {out_path}")
    print(f"可执行位已设置: {', '.join(sorted(EXECUTABLE_NAMES))}")
    return 0


if __name__ == "__main__":
    src = sys.argv[1] if len(sys.argv) > 1 else "artifacts/sc/linux-x64"
    out = sys.argv[2] if len(sys.argv) > 2 else src + ".tar.gz"
    sys.exit(package(src, out))
