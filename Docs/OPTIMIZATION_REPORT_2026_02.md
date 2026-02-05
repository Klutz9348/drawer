# Drawer 项目架构优化报告 (2026-02)

本报告基于 `AGENTS.md` 中新定义的 14 条核心架构原则，对当前 Drawer 项目代码库进行了全面分析，识别出以下关键优化点。

## 1. 架构与模块化 (原则 1, 6)

### 现状
目前 `Features/Drawing` 作为一个单一的大型模块存在。虽然代码层面通过文件夹进行了分层（Domain, App, Presentation），但在物理程序集（Assembly Definition）层面缺乏强制隔离。

### 优化建议
*   **拆分 Assembly Definition**: 将 `Features.Drawing.asmdef` 拆分为：
    *   `Features.Drawing.Core` (Domain Layer - 纯 C#)
    *   `Features.Drawing.App` (Application/Service Layer - 核心逻辑)
    *   `Features.Drawing.Unity` (Presentation Layer - Unity 组件)
*   **收益**: 编译时强制执行分层架构，防止表现层代码逆向依赖业务逻辑，加快编译速度。

## 2. 依赖注入 (原则 4)

### 现状
当前依赖管理主要依靠手动的 `Initialize()` 方法链、`FindObjectOfType` 或单例模式（如 `InputStateManager` 曾紧耦合）。缺乏统一的 IoC 容器。

### 优化建议
*   **引入 DI 容器**: 引入轻量级 DI 框架（如 VContainer 或 Zenject），或实现一个简单的 Composition Root。
*   **统一配置**: 移除 `DrawingAppService` 中查找组件的硬编码逻辑，改由 `Context` 或 `Installer` 进行绑定。
*   **收益**: 提高可测试性，简化对象的创建和生命周期管理。

## 3. 数据持久化 (原则 7)

### 现状
目前项目缺乏明确的“数据访问层”。虽然有 `StrokeSerializer`，但没有看到用于将绘图保存到磁盘或云端的 `Repository` 接口或实现。

### 优化建议
*   **建立 Repository 模式**: 定义 `IDrawingRepository` 接口。
*   **实现本地存储**: 创建基于文件系统的存储实现（如 JSON/Binary），处理保存/加载/列出存档。
*   **收益**: 将数据存储逻辑与业务逻辑解耦，支持未来扩展到云存储。

## 4. 网络层职责分离 (原则 2, 6)

### 现状
`DrawingNetworkService` 目前承担了过多职责：传输层（Socket/Relay）、协议层（压缩/解压）、以及应用层逻辑（Ghost 渲染协调）。

### 优化建议
*   **拆分职责**:
    *   `NetworkTransport`: 仅负责字节发送/接收。
    *   `SynchronizationManager`: 负责状态同步和冲突解决。
    *   `GhostLayerController`: 负责处理远程用户的临时渲染。
*   **收益**: 提高网络模块的可维护性，更容易更换底层传输库（如从 UNet 换到 Photon/Mirror）。

## 5. 工程实践与 CI/CD (原则 12, 13)

### 现状
未发现自动化构建脚本或 CI 配置文件（如 `.github/workflows` 或 `.gitlab-ci.yml`）。代码质量检查依赖人工。

### 优化建议
*   **建立 CI 流水线**: 配置自动化构建和单元测试运行。
*   **引入代码分析**: 添加 `.editorconfig` 和 Roslyn 分析器，自动化执行代码风格检查。
*   **收益**: 减少人为错误，确保主干代码的稳定性。

## 6. 安全性 (原则 8)

### 现状
网络数据包处理缺乏严格的来源验证和加密。虽然有载荷长度检查，但缺乏认证机制。

### 优化建议
*   **增强包验证**: 即使是本地局域网应用，也应增加基本的握手/认证流程。
*   **数据完整性**: 确保所有网络包都经过更严格的边界检查。

## 7. 可观测性 (原则 10)

### 现状
`StructuredLogger` 已经存在，但 `LogPanelController` 在处理大量日志时可能存在性能瓶颈（UI 实例化）。

### 优化建议
*   **UI 虚拟化**: 优化移动端调试控制台，使用无限滚动列表（Virtual List）技术。
*   **远程日志**: 在 `StructuredLogger` 中实现一个 `NetworkSink`，允许在 PC 端实时查看移动端日志。

## 实施路线图

1.  **P0 (高优先级)**: 拆分 Assembly Definition，引入 DI 容器基础。
2.  **P1 (中优先级)**: 实现 Repository 模式（保存/加载功能）。
3.  **P2 (低优先级)**: 搭建 CI/CD 流水线，优化调试控制台。

