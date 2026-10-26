# 测试样本模型

本目录收录了一批开源典型 3D 模型，用于验证查看器各格式加载管线与渲染/拾取效果。
全部文件经 `tests/Avalonia3DViewer.Core.Tests/SampleModelTests.cs` 自动加载与校验
（数据完整性 + 射线拾取），`dotnet test` 时会自动运行（目录缺失则跳过）。

## 模型清单

| 文件 | 格式 | 三角面 | 顶点 | 说明 |
|---|---|---|---|---|
| `teapot.obj` | OBJ (ASCII) | 2,256 | 1,202 | **犹他茶壶**（图形学经典），含法线/UV |
| `bun_zipper.ply` | PLY (ASCII) | 69,451 | 35,947 | **斯坦福兔子**（完整精度） |
| `bunny.stl` | STL (二进制) | 69,451 | 208,353 | **斯坦福兔子**（Blender 导出的二进制 STL） |
| `Box.ply` | PLY (二进制 LE) | 12 | 24 | 二进制 PLY，**纯 CR 换行**（Rhinoceros 导出），含法线与顶点色 |
| `Duck.glb` | GLB (glTF 2.0) | 4,212 | 2,399 | 经典小黄鸭（Khronos 官方样本） |
| `Avocado.glb` | GLB (glTF 2.0) | 682 | 406 | 牛油果（Khronos 官方样本） |
| `WaterBottle.glb` | GLB (glTF 2.0) | 4,510 | 2,549 | 水壶（含顶点色） |
| `box.3mf` | 3MF | 12 | 8 | 基础立方体（3MF Consortium 官方样本） |
| `torus.3mf` | 3MF | 2,200 | 1,100 | 圆环 |
| `cube_gears.3mf` | 3MF | 25,692 | 12,864 | ** cube_gears（多零件齿轮装配体）** |
| `heartgears.3mf` | 3MF | 30,636 | 15,186 | 心形齿轮装配体 |
| `rhombicuboctahedron_color.3mf` | 3MF | 44 | 24 | 彩色菱形立方八面体 |
| `kinect_scan.3mf` | 3MF | 282,544 | 141,249 | Kinect 实物扫描（大网格压力测试） |

## 来源与许可证

- **teapot.obj** — [McNopper/OpenGL](https://github.com/McNopper/OpenGL)（`Binaries/teapot.obj`），教学用途
- **bun_zipper.ply / bunny.stl** — [Google Draco](https://github.com/google/draco) `testdata/`（斯坦福 3D 扫描库，CC BY-NC 学术用途）
- **Box.ply** — Google Draco `testdata/`（Rhinoceros 导出样本）
- **Duck.glb / Avocado.glb / WaterBottle.glb** — [Khronos glTF Sample Models](https://github.com/KhronosGroup/glTF-Sample-Models)，各模型许可证见原仓库（多为 CC0 / CC BY）
- ***.3mf** — [3MF Consortium 3mf-samples](https://github.com/3MFConsortium/3mf-samples) 官方样本

> 这些样本仅用于开发测试；商业使用请遵守各自许可证。

## 复现下载

```bash
mkdir -p samples
# 茶壶（OBJ）
curl -L -o samples/teapot.obj "https://cdn.jsdelivr.net/gh/McNopper/OpenGL@master/Binaries/teapot.obj"
# 斯坦福兔子（ASCII PLY + 二进制 STL）
curl -L -o samples/bun_zipper.ply "https://cdn.jsdelivr.net/gh/google/draco@master/testdata/bun_zipper.ply"
curl -L -o samples/bunny.stl "https://cdn.jsdelivr.net/gh/google/draco@master/testdata/STL/bunny.stl"
# 二进制 PLY（纯 CR 换行）
curl -L -o samples/Box.ply "https://cdn.jsdelivr.net/gh/google/draco@master/testdata/Box.ply"
# Khronos GLB 样本
for m in Duck Avocado WaterBottle; do
  curl -L -o "samples/$m.glb" "https://cdn.jsdelivr.net/gh/KhronosGroup/glTF-Sample-Models@master/2.0/$m/glTF-Binary/$m.glb"
done
# 3MF 官方样本
for m in core/box core/torus core/cube_gears core/heartgears material/rhombicuboctahedron_color material/kinect_scan; do
  curl -L -o "samples/$(basename $m).3mf" "https://cdn.jsdelivr.net/gh/3MFConsortium/3mf-samples@master/examples/$m.3mf"
done
```
