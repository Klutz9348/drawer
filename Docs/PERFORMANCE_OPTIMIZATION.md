# 移动端性能优化报告与实施方案

## 1. 优化概览

本报告详细记录了针对 iOS 和 Android 平台的性能优化措施。已完成核心架构的调整，主要集中在**启动速度**、**包体积**和**渲染性能**。

| 优化维度 | 优化项 | 状态 | 预期收益 |
| :--- | :--- | :--- | :--- |
| **启动速度** | CanvasRenderer 异步初始化 | ✅ 已实施 | 首屏加载不卡顿，分散主线程压力 |
| **启动速度** | Shader 预热 (Warmup) | ✅ 已实施 | 消除首次绘制时的掉帧 |
| **包体积** | 模块裁剪 (Module Stripping) | ✅ 已实施 | 减少 ~5-10MB 包体积 (移除 Physics/Terrain/AI) |
| **包体积** | 资源清理 | ✅ 已实施 | 移除 TextMeshPro 示例与无用 Git 依赖 |
| **构建配置** | iOS Bitcode 禁用 | ✅ 已实施 | 加快构建/上传速度，避免 Linker 错误 |
| **构建配置** | IL2CPP + High Stripping | ✅ 已实施 | 最大化代码执行效率与最小化二进制体积 |
| **Android** | Minify (R8) 启用 | ✅ 已实施 | 混淆与代码压缩 |

---

## 2. 详细实施说明

### 2.1 启动时间优化 (Startup Time)

**问题分析**: 
原 `DrawingAppService` 在 `Awake` 中同步调用 `CanvasRenderer.Initialize`，其中包含 `RenderTexture` 的分配（大内存操作）和 `Shader.Warmup`（GPU 编译耗时），导致应用启动时黑屏或卡顿时间较长。

**优化方案**:
*   **异步初始化管道**: 将初始化逻辑重构为 `IEnumerator InitializeAsync()`。
*   **分帧执行**: 
    1.  第一帧：加载 Compute Shader。
    2.  第二帧：分配 RenderTexture (2048x2048)。
    3.  第三帧：预热 Shader 变体。
    4.  第四帧：初始化材质与 Mesh。
*   **代码位置**: `CanvasRenderer.cs` 和 `DrawingAppService.cs`。

### 2.2 包体积与依赖优化 (Package Size)

**问题分析**: 
`manifest.json` 中包含大量未使用的 Unity 内置模块（如 3D Physics, Particle System, Terrain），增加了最终包体积。此外，`TextMesh Pro` 的示例文件夹包含大量未使用的 Texture 和 Mesh。

**优化方案**:
*   **模块裁剪**: 从 `manifest.json` 移除了 `modules.physics`, `modules.terrain`, `modules.ai` 等 15 个模块。
*   **资源清理**: 删除了 `Assets/TextMesh Pro/Examples & Extras`。
*   **依赖修复**: 移除了连接超时的 `com.coplaydev.unity-mcp` 包，确保构建流程畅通。

### 2.3 iOS 平台特定优化

**构建设置 (ProjectSettings)**:
*   **Scripting Backend**: 强制 **IL2CPP**。
*   **Architecture**: **ARM64**。
*   **Managed Stripping Level**: **High** (最高级别代码裁剪)。
*   **Graphics API**: 强制仅使用 **Metal** (移除 OpenGL ES 遗留代码)。

**后处理脚本 (Post-Processing)**:
*   创建了 `Assets/Scripts/Editor/IOSBuildPostProcessor.cs`。
*   自动将 Xcode 工程的 **Enable Bitcode** 设置为 **NO**。

### 2.4 Android 平台特定优化

**构建设置**:
*   **Minify**: Release 模式下启用 **R8 (ProGuard)**。
*   **Scripting Backend**: **IL2CPP**。

---

## 3. 后续监控与建议

### 3.1 性能监控体系 (已准备就绪)
项目已集成 `PerformanceMonitor` 和 `StructuredLogger`。建议在真机测试时关注以下日志：
*   `[PerformanceHeartbeat]`: 每秒输出 FPS 和内存占用。
*   `TraceId`: 用于追踪单次绘制操作的完整生命周期耗时。

### 3.2 建议测试用例
1.  **冷启动测试**: 杀进程后重新打开，记录到出现白色画布的时间（目标 < 2秒）。
2.  **长绘制压力测试**: 连续快速绘制 100 笔，观察 FPS 是否稳定在 60。
3.  **内存警告测试**: 在低端机型（如 iPhone 8）上反复 Clear/Draw，观察是否 OOM。

### 3.3 潜在风险
*   **High Stripping**: 由于开启了最高级别的代码裁剪，如果使用了反射（Reflection）且未在 `link.xml` 中声明，可能会导致运行时 Crash。请重点测试 JSON 序列化和依赖注入功能。
