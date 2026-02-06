| 2.2 | 资源清理 | 移除未引用的材质和 Shader 变体 | P2 | [已完成] (Cleaned up shaders & variants) |
| 2.3 | 诊断模块降级 | `LogPanelController` 和 `PerformanceMonitor` 仅在 Debug 模式编译 | P2 | [已完成] (Conditional compilation applied) |

### 第三阶段：架构重构 (Phase 3: Architecture Refactoring)
| ID | 任务 | 说明 | 优先级 | 状态 |
| :--- | :--- | :--- | :--- | :--- |
| 3.1 | DrawingContext 优化 | 优化依赖注入流程，减少 `FindObjectOfType` 调用 | P1 | [已完成] (Optimized FindRenderer) |
| 3.2 | 输入模块解耦 | `StrokeInputHandler` 不再直接依赖 NetworkService | P0 | [已完成] (Event-driven architecture) |

# 5. 未来扩展性规划 (Future Roadmap)

## 5.1 多人绘图 (Multi-User Drawing)

得益于 `IStrokeEventListener` 接口和 `StrokeEntity.AuthorId` 的设计，接入多人绘图功能无需修改核心代码。

**实施步骤：**
1.  **创建 NetworkClient**：实现网络连接（WebSocket / Photon / Mirror）。
2.  **实现 IStrokeEventListener**：
    ```csharp
    public class NetworkStrokeSender : IStrokeEventListener {
        public void OnStrokeStarted(StrokeEntity stroke) {
            NetworkClient.Send("StrokeStart", stroke.Serialize());
        }
        public void OnStrokeUpdated(StrokeEntity stroke, LogicPoint point) {
            NetworkClient.Send("StrokeMove", point);
        }
        public void OnStrokeCompleted(StrokeEntity stroke) {
            NetworkClient.Send("StrokeEnd", stroke.Id);
        }
    }
    ```
3.  **接收网络数据**：
    ```csharp
    void OnNetworkMessageReceived(string msgType, byte[] data) {
        if (msgType == "StrokeStart") {
            var strokeData = Deserialize(data);
            // 调用核心接口，传入远程玩家 ID
            _drawingFacade.StartStroke(strokeData.StartPoint, strokeData.AuthorId);
        }
    }
    ```
4.  **注册插件**：在 `DrawingContext` 或启动脚本中注册：
    ```csharp
    _drawingAppService.RegisterStrokeListener(new NetworkStrokeSender());
    ```

## 5.2 绘图回放 (Replay System)
原理同上，创建一个 `ReplayRecorder` 实现 `IStrokeEventListener` 将数据存入文件。回放时读取文件并按时间戳调用 `StartStroke/MoveStroke`。

## 5.3 AI 辅助 (AI Assistant)
通过监听 `OnStrokeCompleted`，将笔画数据发送给 AI 识别服务，识别用户意图（如“你在画猫吗？”）。
