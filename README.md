# Avalonia3D 工业看图 · 打标 Demo

基于 **.NET 10 + Avalonia 12 + OpenGL** 的跨平台 3D 模型加载、显示与标注（打标）演示程序。
定位为**工业级 3D 打标软件**的最小可用内核：完整的模型加载管线、轨道相机交互、表面拾取、
标注/测距与数据导出，且**完全支持 NativeAOT 发布**（零反射、全裁剪，已验证）。

对标微软 **3D 看图（View3D）** 的渲染与交互体验：平滑阻尼轨道相机、地面网格与坐标三角、
多光源着色、抗锯齿（2x SSAA）、线框/点云视图、标准视图、适应视图。
## 功能特性

| 模块 | 能力 |
| --- | --- |
| 模型加载 | STL（ASCII/二进制自动识别）、OBJ（含逐顶点颜色）、PLY（ASCII/二进制LE/BE）、3MF（单位换算）、GLB（glTF 2.0 静态网格） |
| 渲染 | OpenGL 3.3 / GLES3 / GLES2 自适应着色器（版本自动降级）、Blinn-Phong 多光源、2x SSAA 抗锯齿 |
| 场景 | 自适应地面网格（距离淡出）、彩色坐标轴、屏幕空间坐标三角 |
| 交互 | 左键轨道旋转 / 右键·中键平移 / 滚轮缩放 / 双击聚焦 / 标准视图 / 适应视图 / **拖动移动模型坐标（G，Shift 锁轴）** |
| 拾取 | CPU 射线拾取（均匀网格加速 + Möller–Trumbore，双面命中） |
| 标注（打标） | 标注模式单击模型放置编号标记；相邻点自动测距；标注列表；JSON 导出；Delete 删除 / Esc 退出 |
| 稳定性 | 异步加载 + 错误提示、GL 异常降级保护、上下文丢失自动重建、坏文件不崩溃 |
| 测试样本 | 13 个开源典型模型（犹他茶壶 OBJ、斯坦福兔子 STL/PLY、Khronos GLB、3MF 官方样本），自动加载+拾取校验 |
| 工程 | 56 个单元测试全绿；Release 发布即 NativeAOT（win-x64 已验证） |

## 快速开始

### 环境要求

- .NET 10 SDK（10.0.402 验证通过）
- 支持 OpenGL 3.3 / OpenGL ES 3.0 的 GPU（或软件光栅化 fallback）
- NativeAOT 发布需要平台原生工具链（见下文）

### 运行（Debug）

```bash
dotnet run --project src/Avalonia3DViewer.Desktop
```

启动后自动加载内置「直齿轮」示例模型。也可通过工具栏打开本地文件，
或把 `.stl/.obj/.ply/.3mf/.glb` 文件**直接拖入窗口**。

### 操作说明

| 操作 | 效果 |
| --- | --- |
| 左键拖拽 | 轨道旋转 |
| 右键 / 中键拖拽 | 平移视图 |
| 滚轮 | 缩放（指数） |
| **G** | **切换移动模型模式**（与标注模式互斥） |
| 移动模式下左键拖拽 | **拖动模型改变世界坐标**（经面向相机的抓取平面转换为世界位移，实时显示 X/Y/Z） |
| 移动模式 + Shift 拖拽 | 锁定为屏幕水平 / 垂直单向移动 |
| 双击模型 | 聚焦到该点 |
| F / R | 适应视图 / 重置视角 |
| Delete | 删除选中标注 |
| Esc | 退出标注 / 移动模式 |

> 移动模型时：网格/线框/点云经 `uModel` 平移矩阵渲染，地面网格保持世界坐标不动；
> 标注点随模型表面一起移动；拾取射线自动转换到模型局部空间，点击/标注/聚焦仍准确。

## 架构

```
Avalonia3DViewer.sln
├── src/Avalonia3DViewer.Core/          # 纯托管核心（零第三方依赖，IsAotCompatible）
│   ├── Geometry/                       # MeshData / BoundingBox / Ray
│   ├── Loading/                        # STL/OBJ/PLY/3MF/GLB 加载器（手写解析，无反射）
│   ├── Camera/OrbitCamera.cs           # 轨道相机 + 阻尼 + 投影/射线数学
│   ├── Scene/ModelTransform.cs         # 模型世界坐标变换（拖动改坐标 + 拾取/标注坐标转换）
│   ├── Picking/MeshRaycaster.cs        # 均匀网格加速射线拾取
│   ├── Annotations/                    # 标注集合、测距、JSON 导出
│   └── Procedural/DemoModels.cs        # 内置示例模型（齿轮/阶梯轴/螺母/圆环结）
├── src/Avalonia3DViewer.Desktop/       # Avalonia 12 应用（OpenGL 渲染 + UI）
│   ├── Controls/ViewportControl.cs     # OpenGlControlBase 子类：交互 + 帧循环
│   ├── Rendering/GlRenderer.cs         # GL 渲染器（网格/线框/点/网格/标记/SSAA）
│   ├── Rendering/ShaderSources.cs      # GLSL 源码（现代语法 + 版本预处理器）
│   └── ViewerSession.cs                # 状态中心（UI 线程可变 / 渲染线程只读快照）
├── samples/                           # 开源测试模型（茶壶/兔子/GLB/3MF，见 samples/README.md）
├── tools/test-mouse.ps1               # 真机鼠标交互验证脚本（PowerShell）
└── tests/Avalonia3DViewer.Core.Tests/  # xunit：数学/相机、加载器、拾取、标注、真实样本
```

发布产物目录：`artifacts/native/<rid>/`（NativeAOT，单文件）与 `artifacts/sc/<rid>/`
（自包含非 AOT），见「跨平台发布」章节。

### 关键技术决策

1. **OpenGL 嵌入方式**：使用 Avalonia 内置 `OpenGlControlBase`（`Avalonia.OpenGL` 程序集）
   的 `OnOpenGlInit/OnOpenGlRender/OnOpenGlDeinit` 钩子，无需自建窗口或 Surface 互操作。
   ⚠️ **必须实现 `ICustomHitTest`**：`OpenGlControlBase` 经 Composition SurfaceVisual 渲染，
   不出现在场景图命中测试数据中，默认收不到任何指针事件（Avalonia 已知问题，
   见 AvaloniaUI/Avalonia Discussion #10879；`ViewportControl` 已实现）。
2. **GUI 子系统（OutputType=WinExe）**：Windows 可执行文件的 PE 头 `Subsystem` 字段决定
   双击行为——`Exe` 为控制台子系统（CUI=3），双击时**先弹出黑色控制台窗口再启动程序**；
   `WinExe` 为图形子系统（GUI=2），直接启动、无控制台窗口。本项目使用 `WinExe`
   （可用 `python3 -c` 读 PE 头偏移 `pe+24+68` 验证）。Linux/macOS 无此区分。
3. **GL 能力适配**：`GlInterface` 仅暴露约 80 个入口（无 `glLineWidth`/`glBlendFunc`/
   `glPolygonMode`/`Uniform3f`）。因此：
   - 线框/网格/测距线统一以 **GL_LINES 几何**绘制（深度偏差在顶点着色器中完成）；
   - 不依赖混合，全部不透明渲染；
   - vec3 uniform 通过 mat4 第 4 行传递（`GlProgram.SetVec3`）；
   - 着色器按运行时 GL 版本生成（330 core / 150 / 300 es / 100 es 关键字降级）。
4. **模型坐标变换**：网格以局部坐标上传 GPU，`ModelTransform`（Core）维护世界偏移；
   渲染侧各着色器新增 `uModel`（平移矩阵，随 `CameraSnapshot` 不可变快照下发），
   拾取侧把世界射线转到局部空间、命中点转回世界坐标保存，标注随模型整体平移。
   System.Numerics 为行向量约定（平移量在 M41/M42/M43），上传 GL（transpose=false）
   后恰好落在 GLSL `uModel * vec4(p,1)` 期望的第 4 列。
5. **NativeAOT 友好**：
   - Core 零反射、零第三方包，全部手写二进制/XML 解析（`BinaryPrimitives`、`XmlReader`、
     `JsonDocument`）；
   - UI 使用编译绑定（`x:DataType`），无反射绑定；
   - 视图矩阵按 System.Numerics 行向量约定手工构建并上传（`transpose=false`），
     GLSL 侧以 `uProj*uView*uModel` 列向量顺序使用；
   - 投影矩阵按 OpenGL `[-1,1]` 深度区间手工构建（System.Numerics 自带的是 DX `[0,1]` 区间）。
6. **线程模型**：相机/标注等可变状态全部在 UI 线程维护；渲染线程只读取不可变快照
   （`CameraSnapshot` 记录结构经引用交换发布，标注数组同样不可变交换）。

## 跨平台发布（x64 / ARM64）

### 发布矩阵（在 Windows x64 开发机上的实际验证结果）

| 目标 RID | AOT（单文件原生） | 自包含（非 AOT） | 本机可构建 | 产物校验 |
| --- | --- | --- | --- | --- |
| **win-x64** | ✅ 已验证 | — | ✅ | 21.6 MB 原生 exe，实机冒烟通过 |
| **win-arm64** | ❌ 缺 ARM64 C++ 工具链 | ✅ 已验证 | ✅ | PE machine=0xAA64，GUI 子系统 |
| **linux-x64** | ❌ 跨 OS 编译不支持 | ✅ 已验证 | ✅ | ELF machine=0x3E（x86-64），16 个原生 .so |
| **linux-arm64** | ❌ 跨 OS 编译不支持 | ✅ 已验证 | ✅ | ELF machine=0xB7（AArch64），16 个原生 .so |
| osx-arm64 | ❌ 需 macOS | ✅ 可构建 | ✅ | 需在 macOS 上运行 |

> **AOT = NativeAOT**（Release 配置默认开启，`PublishAot`+`TrimMode=full`）。
> **自包含** = 自带 .NET 运行时（`--self-contained true`），无需目标机安装 .NET，但仍带 JIT。
> 本文「已验证」均在本机实际执行并校验了产物文件头（PE/ELF machine 字段）。

### 1) Windows x64 — NativeAOT（本机已验证）

```bash
dotnet publish src/Avalonia3DViewer.Desktop -c Release -r win-x64 -o artifacts/native/win-x64
```

产出单一原生可执行文件（无 JIT、启动快、体积小）。产物位于
`artifacts/native/win-x64/Avalonia3DViewer.exe`（约 21 MB，需随目录分发
`av_libglesv2.dll` / `libHarfBuzzSharp.dll` / `libSkiaSharp.dll` 三个原生库）。

> 注：需要安装「使用 C++ 的桌面开发」工作负载（提供 `link.exe` 与 Windows SDK）。
> 若链接阶段报 `LNK1181: 无法打开 advapi32.lib`，请设置 SDK 库路径，例如：
> ```bash
> export LIB="D:\Windows Kits\10\Lib\<SDK版本>\um\x64;D:\Windows Kits\10\Lib\<SDK版本>\ucrt\x64;<VS>\VC\Tools\MSVC\<版本>\lib\x64"
> ```

### 2) Windows on ARM（win-arm64）— 自包含（本机已验证）

```bash
dotnet publish src/Avalonia3DViewer.Desktop -c Release -r win-arm64 \
    --self-contained true -p:PublishAot=false -o artifacts/sc/win-arm64
```

产物 `artifacts/sc/win-arm64/`（约 127 MB，含 ARM64 的 `av_libglesv2.dll` /
`libSkiaSharp.dll` / `libHarfBuzzSharp.dll`）。在 Surface Pro X / Snapdragon X Elite
等 Windows ARM 设备上直接运行，无需 x64 模拟。

**win-arm64 的 AOT 编译还需额外条件**（本机不满足，故未验证）：
- 安装 VS 组件 **「MSVC C++ ARM64 生成工具」**（组件 ID `Microsoft.VisualStudio.Component.VC.Tools.ARM64`）。
  命令行方式：在 VS Installer 目录执行
  `vs_installer modify --installPath "<VS路径>" --add Microsoft.VisualStudio.Component.VC.Tools.ARM64`
  （或在 VS Installer GUI 的「单个组件」中勾选）。
- 装好后设置 ARM64 库路径再发布：
  ```bash
  export LIB="D:\Windows Kits\10\Lib\<SDK版本>\um\arm64;D:\Windows Kits\10\Lib\<SDK版本>\ucrt\arm64;<VS>\VC\Tools\MSVC\<版本>\lib\arm64"
  dotnet publish src/Avalonia3DViewer.Desktop -c Release -r win-arm64 -o artifacts/native/win-arm64
  ```
- 仅缺链接器时也可用 `-p:IlcUseEnvironmentalTools=true` 让 ILC 使用 PATH 上的 `link.exe`
  （ILC 用 RyuJIT 直接产出 ARM64 对象码，本身不需要 ARM64 编译器）；但仍需 ARM64 CRT 静态库
  （`LIBCMT.lib` 等），所以本质上仍需上面的 VS 组件。
- 无 Windows ARM 机器时的替代方案：GitHub Actions `windows-11-arm` runner。

### 3) Linux x64 / ARM64 — 自包含（本机已验证，跨平台发布）

```bash
# linux-x64
dotnet publish src/Avalonia3DViewer.Desktop -c Release -r linux-x64 \
    --self-contained true -p:PublishAot=false -o artifacts/sc/linux-x64

# linux-arm64（树莓派 64 位、飞腾/鲲鹏等 ARM 服务器）
dotnet publish src/Avalonia3DViewer.Desktop -c Release -r linux-arm64 \
    --self-contained true -p:PublishAot=false -o artifacts/sc/linux-arm64
```

产物分别为 `artifacts/sc/linux-x64/`（约 102 MB）与 `artifacts/sc/linux-arm64/`（约 107 MB），
内含对应架构的 `libcoreclr.so` / `libclrjit.so` / `libSkiaSharp.so` 等 16 个原生库，
拷贝到目标 Linux 机器 `chmod +x Avalonia3DViewer && ./Avalonia3DViewer` 即可
（需目标机有 X11/Wayland 与 GL 驱动；Linux 不使用 Windows 的 av_libglesv2 库，
Skia 直接走系统 EGL/GLES）。

> 已打包 `artifacts/sc/linux-x64.tar.gz` / `linux-arm64.tar.gz`（apphost 可执行位已预设 0755，
> 由 `tools/package-linux.py` 生成）便于传输。

### 4) 在 Ubuntu 上运行（以 linux-x64 为例，arm64 同理）

```bash
# 1) 传输：Windows PowerShell 直接 scp（Ubuntu 需已安装并启动 sshd）
scp D:\Avalonia3D\artifacts\sc\linux-x64.tar.gz user@<ubuntu-ip>:~/
#    或 U 盘 / 共享目录 / 双系统时直接 cp -r /mnt/d/Avalonia3D/artifacts/sc/linux-x64 ~/

# 2) 解压（tar.gz 内 apphost 已带可执行位；若整个目录直接拷贝则需先 chmod +x）
tar -xzf linux-x64.tar.gz && cd linux-x64
chmod +x Avalonia3DViewer   # 目录拷贝方式必需；tar.gz 方式已内置，可忽略

# 3) 安装运行依赖（桌面版 Ubuntu 默认都有；最小安装/服务器版才需要手动装）
sudo apt update
sudo apt install -y libstdc++6 libfontconfig1 fontconfig \
     libegl1 libgl1 libglesv2 libx11-6 libxcursor1 libxrandr2

# 4) 运行
./Avalonia3DViewer
```

> ⚠️ **双击报“无法显示 / 没有安装可执行文件文件的查看器”**：这是 GNOME 文件管理器
> （Nautilus）拒绝执行**没有可执行位**的文件——.NET 在 Windows 上 publish 不会给 Linux
> apphost 设置 `+x`（产物是 `rw-r--r--`）。解决：先 `chmod +x Avalonia3DViewer` 再双击，
> 或直接在终端运行。用 `tools/package-linux.py` 打的 tar.gz 已预设 0755，解压即用。

#### Linux 上 3D 视口空白 / FPS 显示 0 的排查

程序能启动、UI 正常，但左侧视口一片空白且状态栏 FPS 为 `-- FPS`，说明
`OnOpenGlInit`/`OnOpenGlRender` 从未被调用。本应用已内置诊断：超过 3 秒未完成 GL 初始化时，
视口会显示深色浮层写明原因。常见原因与处理：

| 原因 | 处理 |
| --- | --- |
| 虚拟机未启用 3D 加速 | VirtualBox：设置 → 显示 → 屏幕 → **启用 3D 加速**（显存拉满）；VMware 同理 |
| 缺少 Mesa 软件渲染 | `sudo apt install libgl1-mesa-dri libegl-mesa0 mesa-vulkan-drivers` |
| 远程/无显示器会话 | `xvfb-run -a -s "-screen 0 1600x900x24" ./Avalonia3DViewer` |
| GL 驱动过老 | `glxinfo -B` 查看；需要 OpenGL 3.3 / GLES 3.0 以上 |

终端直接运行可看到 Avalonia 的 OpenGL 日志（`Unable to obtain Compositor instance` /
`current platform doesn't support OpenGL interop with the compositor` 等）。

实现层面注意（已修复）：`OpenGlControlBase.RequestNextFrameRendering()` **不能在 GL 初始化
完成前调用**——Avalonia 已知缺陷（#13879）会把内部 `_updateQueued` 置位但合成器未就绪，
之后首帧请求永久丢失，症状正是"视口空白 + FPS 0"。本项目所有帧请求统一走
`ViewportControl.RequestFrame()`（初始化前为空操作），并配合 UI 线程首帧兜底与看门狗浮层。

依赖说明（解析产物 ELF `DT_NEEDED` 得到）：`libstdc++.so.6` / `libgcc_s.so.1`（C++ 运行时）、
`libfontconfig.so.1`（Skia 字体发现），其余 `libc/libm/libdl/libpthread/librt` 为 glibc 自带。
Skia 的 GL 后端（`libEGL.so.1` / `libGLESv2.so.1`）是运行时动态加载，缺失时自动降级为 CPU
软件渲染（能跑但慢）。Avalonia 12 的 Linux X11 后端为全托管实现，**不需要**
`libAvaloniaNative.so`。

无显示器环境（SSH / 服务器）：

```bash
sudo apt install -y xvfb
xvfb-run -a -s "-screen 0 1600x900x24" ./Avalonia3DViewer
```

虚拟机（VMware/VirtualBox）里运行请开启「3D 加速」，否则只有软件渲染。

**Linux 的 AOT 编译不支持在 Windows 上交叉进行**（ILC 明确报错
`Cross-OS native compilation is not supported`），需在 Linux 环境中构建：

```bash
# 方式 A：任意机器的 Docker（.NET SDK 镜像自带 clang）
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet publish src/Avalonia3DViewer.Desktop -c Release -r linux-arm64 -o artifacts/native/linux-arm64

# 方式 B：WSL2 / 原生 Linux
sudo apt-get update && sudo apt-get install -y clang zlib1g-dev
dotnet publish src/Avalonia3DViewer.Desktop -c Release -r linux-arm64 -o artifacts/native/linux-arm64

# 方式 C：GitHub Actions（公开仓库免费）
# runs-on: ubuntu-24.04-arm
```

> glibc 兼容性提示：AOT 产物链接到构建机的 glibc。面向旧发行版时，请在较老的系统上构建
> （如 Ubuntu 22.04），否则在新系统上构建的二进制无法在旧系统运行。
> macOS（osx-arm64）AOT 需在 macOS 上安装 Xcode 命令行工具后执行（跨 OS 同样不支持）。

### 产物架构自检

```bash
# Windows PE：machine 0x8664=x64, 0xAA64=ARM64；subsystem 2=GUI, 3=控制台
python3 -c "import struct;d=open(r'artifacts/sc/win-arm64/Avalonia3DViewer.exe','rb').read();p=struct.unpack_from('<I',d,0x3c)[0];print(hex(struct.unpack_from('<H',d,p+4)[0]))"
# Linux ELF：machine 0x3E=x86-64, 0xB7=AArch64
python3 -c "import struct;d=open('artifacts/sc/linux-arm64/Avalonia3DViewer','rb').read(20);print(hex(struct.unpack_from('<H',d,18)[0]))"
```

## 测试

```bash
dotnet test
```

覆盖：投影矩阵 NDC 映射、视线射线往返、阻尼收敛、模型坐标变换（矩阵布局/射线往返/标注随移）、五种格式加载器（含二进制陷阱用例）、
拾取命中/顺序/最大距离、边去重、标注编号与测距、JSON 导出、内置模型网格有效性，
以及 `samples/` 目录下全部真实开源模型的加载与拾取校验（含纯 CR 换行的二进制 PLY 等边界用例）。

## 已知限制与路线图（面向工业打标）

- STEP / IGES 等 CAD B-rep 格式需要 OpenCASCADE 绑定（体积大，规划中）；
- 装配体（多节点/多材质）与 GLTF 动画未纳入本 Demo；
- 标注目前为点标注 + 相邻测距；可扩展为面轮廓提取、打标路径规划、激光打标参数绑定；
- 计划：视图截图导出（glReadPixels）、标注随模型坐标系持久化（JSON 双向）、
  大模型 LOD 与分块加载。

## 许可证

MIT（示例代码可自由用于工业项目原型）。
