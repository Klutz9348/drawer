# 架构与设计 (Architecture & Design)

## 1. 系统架构图
项目采用分层架构，严格遵循关注点分离原则。

```mermaid
graph TD
    subgraph Presentation Layer [表现层]
        CanvasRenderer[CanvasRenderer (MonoBehaviour)]
        InputHandler[Input System]
        UI[User Interface]
    end

    subgraph App Layer [应用层]
        DrawingAppService[DrawingAppService (Facade)]
        CommandSystem[Command System]
        InputState[InputStateManager]
        StrokeInput[StrokeInputHandler]
    end

    subgraph Service Layer [领域服务层]
        HistoryManager[DrawingHistoryManager]
        CollisionService[StrokeCollisionService]
        SmoothingService[StrokeSmoothingService]
    end

    subgraph Domain Layer [领域核心层]
        Entity[Stroke Entity / LogicPoint]
        Interfaces[Interfaces (IStrokeRenderer, IStrokeEventListener...)]
    end

    subgraph Extensions [扩展模块 (Plug-ins)]
        NetworkPlugin[Network Plugin (Future)]
        ReplayPlugin[Replay System (Future)]
        AIPlugin[AI Analyzer (Future)]
    end

    InputHandler -->|IDrawingFacade| DrawingAppService
    UI -->|IDrawingFacade| DrawingAppService
    DrawingAppService -->|IStrokeRenderer| CanvasRenderer
    DrawingAppService --> HistoryManager
    DrawingAppService --> CollisionService
    DrawingAppService --> StrokeInput
    
    StrokeInput -->|IStrokeEventListener| Extensions
    Extensions -->|IDrawingFacade.StartStroke(id)| DrawingAppService
    
    HistoryManager --> CommandSystem
    CommandSystem -->|IStrokeRenderer| CanvasRenderer
```

## 2. 核心层级说明

### 2.1 表现层 (Presentation)
*   **职责**：处理 Unity 引擎的具体实现，如 GPU 绘图、输入捕获、UI 展示。
*   **核心类**：
    *   `CanvasRenderer`：负责管理 `CommandBuffer`、材质 (`Material`) 和网格 (`Mesh`)。实现 `IStrokeRenderer` 和 `ICanvasResolutionProvider` 接口。
    *   **通信**：通过 `IDrawingFacade` 接口与应用层交互，避免直接依赖 `DrawingAppService`。

### 2.2 应用层 (App)
*   **职责**：作为系统的外观 (Facade)，协调输入、业务逻辑和渲染。
*   **核心类**：
    *   `DrawingAppService`：主入口，实现 `IDrawingFacade` 和 `IBrushRegistry` 接口。分发 `StartStroke`/`MoveStroke`/`EndStroke` 事件。
    *   `StrokeInputHandler`：核心输入处理器。**支持多作者 (Multi-Author) 输入**。通过 `RegisterListener` 提供扩展能力。
    *   `InputStateManager`：负责管理当前输入状态（笔刷、颜色、大小、橡皮擦模式）并与渲染器同步。

### 2.3 服务层 (Service)
*   **职责**：处理复杂的业务规则和算法。
*   **核心类**：
    *   `DrawingHistoryManager`：管理撤销/重做栈 (`Undo/Redo`)，维护命令历史。
    *   `StrokeCollisionService`：使用空间索引 (`StrokeSpatialIndex`) 优化橡皮擦的碰撞检测。

### 2.4 领域层 (Domain)
*   **职责**：定义核心数据结构和业务实体，不依赖于 Unity 引擎的具体实现（尽可能纯 C#）。
*   **核心类**：
    *   `LogicPoint`：结构体，包含 `x`, `y` (0-65535 ushort), `pressure` (byte)。
    *   `StrokeEntity`：笔画实体，包含点集、颜色、种子、ID、**AuthorID** 等。
    *   **接口**：`IStrokeEventListener` 定义了笔画生命周期事件 (`OnStrokeStarted`, `OnStrokeUpdated`, `OnStrokeCompleted`)，是多人绘图、回放系统的接入点。

## 3. 关键技术选型

### 3.1 多人绘图扩展架构 (Multi-User Architecture)
为了支持“你画我猜”等多用户场景，系统采用了**输入/输出分离**的插件化设计：
*   **输出 (Producer)**: 本地产生的笔画通过 `IStrokeEventListener` 接口广播给所有注册的监听器（如网络发送器）。
*   **输入 (Consumer)**: 来自网络或其他源的笔画，通过调用 `IDrawingFacade.StartStroke(point, authorId)` 注入系统，与本地输入同等对待。
*   **优势**：核心绘图逻辑不需要关心网络实现细节（Photon, Mirror, Socket.IO 等），网络模块完全解耦。

### 3.2 坐标系统
*   **LogicPoint (逻辑坐标)**：使用 `ushort` (0-65535) 存储坐标。
    *   **理由**：解耦渲染分辨率与逻辑数据。无论屏幕是 1080p 还是 4k，逻辑数据保持一致，便于跨设备同步和序列化。

### 3.3 空间哈希 (Spatial Hashing)
*   **实现**：`StrokeSpatialIndex`。
    *   **用途**：将画布划分为网格，快速查询某一点附近的笔画。将橡皮擦的碰撞检测复杂度从 O(N) 降低到局部搜索。
