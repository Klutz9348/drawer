# 架构与技术深度解析

本文档详细介绍了高性能画板背后的内部架构、算法和技术决策。旨在供核心开发人员和希望扩展渲染或数据层的人员参考。

## 1. 核心设计理念

为了实现 "Apple Notes 般" 的流畅度和跨平台一致性，系统基于三大支柱构建：

1.  **网格盖章渲染 (Mesh Stamping Rendering)**: 绕过 Unity 的 `LineRenderer`，转而采用 GPU 实例化的四边形盖章技术，以实现完美的笔触控制。
2.  **确定性逻辑 (Deterministic Logic)**: 所有空间数据都存储在归一化的逻辑坐标系 (0-65535) 中，以确保在不同设备和分辨率下获得相同的结果。
3.  **整洁架构 (Clean Architecture)**: 严格分离领域逻辑、应用服务和表现层（Unity 视图）。

### 1.1 优化概览 (Optimizations)

*   **空间索引**: 使用四叉树 (QuadTree) 加速笔画查询。
*   **GPU 计算**: 利用 Compute Shader 并行生成印章数据。
*   **数据压缩**: `LogicPoint` 结构体紧凑设计 (5 bytes/point)，为网络同步打下基础。

## 2. 坐标系统

理解坐标转换对于开发至关重要。

### 2.1 坐标空间

| 空间 | 范围 | 类型 | 用途 |
| :--- | :--- | :--- | :--- |
| **输入空间** | `Vector2` (Pixel) | `float` | 来自 `Input.mousePosition` 的原始数据 |
| **归一化空间** | `Vector2` (0.0 - 1.0) | `float` | 分辨率无关的中间步骤 |
| **逻辑空间** | `LogicPoint` (0 - 65535) | `ushort` | **存储与网络**。数据的唯一真理源。 |

### 2.2 数据流向

1.  **输入**: `MouseInputProvider` 捕获屏幕像素。
2.  **转换**: 使用 `DrawingConstants.LOGICAL_RESOLUTION` 转换为 `LogicPoint` (0-65535)。
3.  **存储**: 存储在 `StrokeEntity` 中。
4.  **渲染**: 转换回归一化 (0-1) -> 屏幕像素，供 `CommandBuffer` 执行。

## 3. 渲染管线 (网格盖章)

`CanvasRenderer.cs` 实现了自定义的 "Mesh Stamping" 技术。

### 3.1 为什么不用 LineRenderer？

Unity 的 `LineRenderer` 生成三角形带。这在尖角处会产生伪影，并且不支持自然地改变每段的不透明度/纹理。

### 3.2 盖章算法

我们不是连接点，而是沿着路径重复“盖”上笔刷纹理。

1.  **输入**: 平滑点列表 (`LogicPoint`)。
2.  **生成器**: `StrokeStampGenerator` 根据 `SpacingRatio` 计算输入点之间的插值位置。
    *   *示例*: 如果点间距为 10px，间距为 1px，则生成 10 个印章。
3.  **批处理**: 印章被收集到 `StampData` 缓冲区中。
4.  **GPU 实例化**: `CommandBuffer.DrawMesh` 在单个绘制调用（或分批）中绘制数千次简单的四边形网格。
5.  **材质**: 使用 `BrushStamp.shader`，处理：
    *   **纹理**: 笔刷笔尖形状。
    *   **颜色/Alpha**: 通过 `MaterialPropertyBlock` 传递的顶点颜色。
    *   **混合模式**: 支持标准混合和自定义橡皮擦逻辑 (`ReverseSubtract`)。

### 3.3 GPU 加速 (Compute Shader)

对于重绘、撤销/重做或加载大型绘图文件等高负载场景，我们启用 **Compute Shader** (`StrokeGeneration.compute`) 进行并行处理。

*   **数据结构**: `StructuredBuffer<LogicPoint>` (Input) -> `AppendStructuredBuffer<StampData>` (Output).
*   **混合策略**:
    *   **实时绘制**: 使用 CPU (无延迟，无状态开销)。
    *   **批量操作**: 自动切换至 GPU (吞吐量优先)。
*   **对齐**: HLSL 结构体遵循 16 字节对齐，C# 端结构体 (`GpuLogicPoint`) 显式添加 Padding 以匹配。

### 3.4 笔刷系统 (Brush System)

#### 程序化 SDF 笔刷 (Procedural SDF)
为了解决基于纹理的笔刷产生的锯齿问题（通常由低分辨率纹理或缩放伪影引起），我们在 `BrushStamp.shader` 中引入了程序化有向距离场 (Signed Distance Field, SDF) 渲染模式。

**机制**:
片元着色器不再采样纹理，而是计算当前像素到 UV 中心 (0.5, 0.5) 的距离。
*   **完美圆形**: 无论缩放级别或笔刷大小如何，形状在数学上都是完美的。
*   **抗锯齿**: 使用 `smoothstep` 在圆形边缘附近插值 Alpha 值，提供高质量的柔和边缘。

**配置**:
SDF 设置已暴露在 `BrushStrategy` ScriptableObjects 中。

| 属性 | 描述 |
| :--- | :--- |
| `UseProceduralSDF` | 勾选以启用完美圆形渲染。取消勾选以使用 `Main Texture`。 |
| `EdgeSoftness` | 控制边缘平滑度。0.001 = 硬边缘，0.5 = 非常柔和。 |

#### 纹理模式 (Texture Mode)
当取消勾选 `UseProceduralSDF` 时，仍支持原始的基于纹理的渲染。这种模式使用 `Main Texture`，适用于非圆形笔刷或艺术纹理（例如粉笔、铅笔纹理）。

## 4. 平滑算法

来自鼠标或触摸屏的原始输入通常是锯齿状的 (10-60Hz)。为了获得平滑的曲线，我们使用 **Catmull-Rom Splines**。

*   **实现**: `StrokeSmoothingService.cs`
*   **窗口**: 使用 4 个控制点的滑动窗口 ($P_{i-1}, P_i, P_{i+1}, P_{i+2}$)。
*   **插值**: 在 $P_i$ 和 $P_{i+1}$ 之间生成固定步长（默认为 4）。
*   **压力**: 随位置线性插值压力值。

## 5. 领域模型与数据结构

### 5.0 空间索引 (Spatial Indexing)

为了支持高效的橡皮擦和选区操作，引入了四叉树结构。

*   **实现**: `StrokeSpatialIndex` 封装了 `QuadTree<StrokeEntity>`。
*   **机制**: 每个笔画 (`StrokeEntity`) 计算其 AABB 包围盒并插入四叉树。
*   **查询**: 支持矩形区域查询，返回与该区域相交的所有笔画候选集，复杂度从 O(N) 降低至 O(logN)。

### 5.1 LogicPoint (Struct)

针对内存和网络带宽进行了优化。

```csharp
public struct LogicPoint {
    public ushort X;        // 2 bytes (0-65535)
    public ushort Y;        // 2 bytes
    public byte Pressure;   // 1 byte (0-255)
    // 总计: 每点 5 字节 (相比 Vector3 的 12 字节)
}
```

### 5.2 StrokeEntity (Class)

表示单个连续的笔画。

*   **Id**: 唯一标识符。
*   **Seed**: 用于确定性笔刷抖动的随机种子。
*   **Points**: `LogicPoint` 列表。
*   **ColorRGBA**: 用于紧凑存储的 32 位整数颜色。

### 5.3 序列化协议 (Serialization Protocol)

采用自定义二进制格式 (`StrokeSerializer`)，极大优于 JSON/XML。

*   **Header**: `STRK` (Magic) + Version (1 byte).
*   **VarInt**: 使用变长整数编码长度和索引，节省空间。
*   **Delta Encoding**: 笔画中的点存储相对于前一个点的增量 (dx, dy, dp)。
    *   配合 **ZigZag** 编码，有效处理负数增量。
*   **效果**: 平均单点存储大小压缩至 **< 5 字节**。

## 6. 项目结构 (DDD)

```text
Assets/Scripts/Features/Drawing/
├── Domain/              # 纯 C# 逻辑，尽量无 Unity 依赖
│   ├── Entity/          # StrokeEntity
│   ├── ValueObject/     # LogicPoint
│   └── Interface/       # IStrokeRenderer
├── Service/             # 业务逻辑
│   └── StrokeSmoothingService
├── Presentation/        # Unity 视图层
│   ├── CanvasRenderer   # GPU 实现
│   └── MouseInputProvider # 输入处理
└── App/                 # 应用层
    └── DrawingAppService # 外观/控制器
```

## 7. 未来路线图与优化 (Future Roadmap & Optimization)

### 已完成 (Completed) ✅
*   **SDF 抗锯齿笔刷 (SDF Anti-aliasing Brush)**: 基于 Signed Distance Field 的程序化圆生成，彻底消除缩放锯齿。
*   **四叉树空间索引 (QuadTree Spatial Indexing)**: 已集成 `StrokeSpatialIndex`，支持高效的空间查询。
*   **GPU 加速 (GPU Acceleration)**: 已实现 `Compute Shader` 笔触生成，支持混合渲染管线（Hybrid Rendering Pipeline）。
*   **二进制协议 (Binary Protocol)**: 已实现基于 VarInt 和增量编码（Delta Encoding）的 `StrokeSerializer`，平均单点 < 5 字节。

### 待办事项 (Upcoming) 🚀
1.  **无限画布 (Infinite Canvas)**
    *   突破 `LOGICAL_RESOLUTION` (65535) 限制。
    *   引入基于分块 (Chunk) 的动态加载与坐标映射系统。

2.  **多人实时协同 (Real-time Collaboration)**
    *   **混合同步架构 (Hybrid Sync Architecture)**:
        *   **Ghost Layer**: 使用 UDP 流传输实时增量数据 (`UpdateStroke`)，在 `GhostOverlayRenderer` 上绘制“幽灵笔触”。
        *   **Commit Layer**: 笔画结束时 (`EndStroke`)，发送完整数据包并触发 `DrawingAppService.CommitRemoteStroke`，确保最终一致性。
    *   **协议优化**:
        *   **Delta Encoding**: 坐标使用相对前一点的增量存储。
        *   **VarInt**: 小位移（绝大多数情况）仅占用 1 字节。
        *   **带宽**: 相比原始 Vector2，数据量减少约 80%。

3.  **笔迹回放与导出 (Playback & Export)**
    *   基于序列化数据的绘画过程回放。
    *   将矢量笔画导出为 SVG/PDF 格式，实现真正的分辨率无关性。

4.  **笔刷引擎增强 (Brush Engine 2.0)**
    *   支持纹理散射 (Scattering) 和 随机色相/抖动。
    *   支持双重笔刷 (Dual Brush) 纹理合成。
