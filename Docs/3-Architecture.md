# 架构与设计 (Architecture & Design)

## 1. 系统架构图
项目采用分层架构，严格遵循关注点分离原则。

```mermaid
graph TD
    subgraph Presentation Layer [表现层]
        CanvasRenderer[CanvasRenderer (MonoBehaviour)]
        GhostRenderer[GhostOverlayRenderer]
        InputHandler[Input System]
        UI[User Interface]
    end

    subgraph App Layer [应用层]
        DrawingAppService[DrawingAppService (Facade)]
        CommandSystem[Command System]
        InputState[InputStateManager]
    end

    subgraph Service Layer [领域服务层]
        HistoryManager[DrawingHistoryManager]
        CollisionService[StrokeCollisionService]
        SmoothingService[StrokeSmoothingService]
        NetworkService[DrawingNetworkService]
    end

    subgraph Domain Layer [领域核心层]
        Entity[Stroke Entity / LogicPoint]
        Interfaces[Interfaces (IStrokeRenderer, IGhostRenderer...)]
    end

    InputHandler -->|IDrawingFacade| DrawingAppService
    UI -->|IDrawingFacade| DrawingAppService
    DrawingAppService -->|IStrokeRenderer| CanvasRenderer
    DrawingAppService --> HistoryManager
    DrawingAppService --> CollisionService
    DrawingAppService -->|Network Events| NetworkService
    NetworkService -->|IGhostRenderer| GhostRenderer
    HistoryManager --> CommandSystem
    CommandSystem -->|IStrokeRenderer| CanvasRenderer
```

## 2. 核心层级说明

### 2.1 表现层 (Presentation)
*   **职责**：处理 Unity 引擎的具体实现，如 GPU 绘图、输入捕获、UI 展示。
*   **核心类**：
    *   `CanvasRenderer`：负责管理 `CommandBuffer`、材质 (`Material`) 和网格 (`Mesh`)。实现 `IStrokeRenderer` 和 `ICanvasResolutionProvider` 接口。
    *   `GhostOverlayRenderer`：实现 `IGhostRenderer` 接口，负责网络预测笔画的临时渲染。
    *   **优化**：使用 `ShaderVariantCollection` 进行预热。
    *   **通信**：通过 `IDrawingFacade` 接口与应用层交互，避免直接依赖 `DrawingAppService`。

### 2.2 应用层 (App)
*   **职责**：作为系统的外观 (Facade)，协调输入、业务逻辑和渲染。
*   **核心类**：
    *   `DrawingAppService`：主入口，实现 `IDrawingFacade` 和 `IBrushRegistry` 接口。分发 `StartStroke`/`MoveStroke`/`EndStroke` 事件。
    *   `InputStateManager`：负责管理当前输入状态（笔刷、颜色、大小、橡皮擦模式）并与渲染器同步。
    *   **诊断**：集成了 `StructuredLogger` 和 `PerformanceMonitor`。

### 2.3 服务层 (Service)
*   **职责**：处理复杂的业务规则和算法。
*   **核心类**：
    *   `DrawingHistoryManager`：管理撤销/重做栈 (`Undo/Redo`)，维护命令历史。
    *   `StrokeCollisionService`：使用空间索引 (`StrokeSpatialIndex`) 优化橡皮擦的碰撞检测。
    *   `DrawingNetworkService`：处理网络同步。不直接依赖 `DrawingAppService`，而是通过事件 (`OnRemoteStrokeCommitted`) 回调。使用 `IGhostRenderer` 进行幽灵笔画渲染。

### 2.4 领域层 (Domain)
*   **职责**：定义核心数据结构和业务实体，不依赖于 Unity 引擎的具体实现（尽可能纯 C#）。
*   **核心类**：
    *   `LogicPoint`：结构体，包含 `x`, `y` (0-65535 ushort), `pressure` (byte)。
    *   `StrokeEntity`：笔画实体，包含点集、颜色、种子、ID 等。

## 3. 关键技术选型

### 3.1 坐标系统
*   **LogicPoint (逻辑坐标)**：使用 `ushort` (0-65535) 存储坐标。
    *   **理由**：解耦渲染分辨率与逻辑数据。无论屏幕是 1080p 还是 4k，逻辑数据保持一致，便于跨设备同步和序列化。
    *   **转换**：通过 `DrawingConstants.LOGIC_TO_WORLD_RATIO` 或动态计算的比率映射到世界坐标。

### 3.2 命令模式 (Command Pattern)
*   **实现**：`ICommand` 接口 (`Execute`, `Undo`)。
*   **用途**：所有绘图操作（画笔、橡皮擦、清屏）都封装为命令。这使得撤销/重做变得简单且健壮。

### 3.3 空间哈希 (Spatial Hashing)
*   **实现**：`StrokeSpatialIndex`。
*   **用途**：将画布划分为网格，快速查询某一点附近的笔画。将橡皮擦的碰撞检测复杂度从 O(N) 降低到局部搜索。

### 3.4 诊断与监控
*   **实现**：`Common.Diagnostics` 命名空间。
*   **特性**：
    *   **结构化日志**：JSON 格式，便于 ELK 等系统采集。
    *   **链路追踪**：`TraceContext` 贯穿笔画生命周期。
    *   **零分配**：使用 `StringBuilder` 缓存和复用，避免 GC。
