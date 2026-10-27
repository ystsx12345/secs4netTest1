using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Secs4Net;
using static Secs4Net.Item;

namespace secs4netTest1
{
    /// <summary>
    /// 设备端（Equipment）SECS/GEM 服务封装。
    /// 
    /// 职责边界（非常重要）：
    /// - 本类只负责“连接管理 + 接收循环 + 消息路由 + 发送辅助”。
    /// - 具体的某条 SxFy 怎么回复、报文结构怎么组，应放到各个 Handler 类里
    ///   （例如 ControlMessageHandler、RecipeMessageHandler）。
    /// - secs4net 只提供 HSMS 传输和 SECS-II 编解码，不提供 GEM 业务表
    ///   （变量表/报告表/事件表/报警表），所以 S1F3、S2F41、S7、S6F11 等
    ///   都需要我们自己构建 SecsMessage 并回复。
    /// 
    /// 线程模型：
    /// - Start() 在 UI 线程调用，只做初始化和启动监听，很快返回。
    /// - 接收循环跑在线程池线程上（Task.Run + GetPrimaryMessageAsync）。
    /// - 所有日志通过 LogReceived 事件抛出，由 UI 层负责 Dispatcher 切线程。
    /// </summary>
    public class SecsEquipmentService : IAsyncDisposable
    {
        /// <summary>
        /// secs4net 的核心对象：负责 SECS-II 消息的发送/接收、事务管理（T3 超时等）。
        /// 它依赖 HsmsConnection 提供的底层连接。
        /// </summary>
        private SecsGem? _secsGem;

        /// <summary>
        /// HSMS 连接对象：负责 TCP 监听/连接、HSMS 握手（Select）、心跳（T8）等。
        /// 被动模式下它监听端口，等待 Host 连入。
        /// </summary>
        private HsmsConnection? _connection;

        /// <summary>
        /// 日志适配器：把 secs4net 内部的日志回调转成我们的 LogReceived 事件。
        /// </summary>
        private SimpleSecsLogger? _logger;

        /// <summary>
        /// 用于取消接收循环。DisposeAsync 时 Cancel，让 await foreach 退出。
        /// </summary>
        private CancellationTokenSource? _receiveCts;

        /// <summary>
        /// 日志“快递员”事件。后台线程产生日志时 Invoke 它，
        /// UI 层订阅后负责切回 UI 线程并更新界面。
        /// 
        /// 为什么用 event 而不是直接传一个 Action 进构造函数？
        /// 因为 event 允许 UI 层在任意时刻订阅/取消订阅，且服务层不需要知道 UI 的存在，
        /// 保持了解耦。
        /// </summary>
        public event Action<SecsLogItem>? LogReceived;

        /// <summary>
        /// 启动设备端监听。
        /// </summary>
        /// <param name="ip">本机要绑定的监听 IP（被动模式）。</param>
        /// <param name="port">HSMS 监听端口。</param>
        /// <param name="deviceId">SECS 设备 ID，必须与 Host 配置一致。</param>
        public void Start(string ip, int port, ushort deviceId)
        {
            // SecsGemOptions 是 secs4net 的配置对象。
            // 这里用 Options.Create 包一层，是因为 SecsGem/HsmsConnection 的构造函数
            // 签名要求 IOptions<SecsGemOptions>（为了兼容 .NET 的 Options 模式），
            // 即使我们不用 DI 容器，也可以手动用 Options.Create 创建。
            var options = Options.Create(new SecsGemOptions
            {
                DeviceId = deviceId,

                // IsActive = false 表示“被动模式”：本端作为 Equipment 监听端口，
                // 等待 Host 主动连过来。如果为 true，则是主动去连 Host（Host 端常用）。
                IsActive = false,

                IpAddress = ip,
                Port = port,

                // 以下是 HSMS 标准里定义的各种超时（单位：毫秒）。
                // 这些值决定了通信的健壮性，一般保持默认或按 Host 要求调整。
                T3 = 45000,  // 等待回复的超时（SendAsync 等回复最多等这么久）
                T5 = 10000,  // 连接建立后等待 Select 响应的超时
                T6 = 5000,   // 等待 Select 请求的超时（被动方）
                T7 = 10000,  // 等待 Not Select 的超时
                T8 = 5000    // 心跳间隔（LinkTest），防止连接假死
            });

            // 创建日志适配器。secs4net 内部会在收发消息、连接状态变化时调用它。
            // 这里把日志字符串包装成 SecsLogItem（Direction=Info），再通过事件抛出。
            _logger = new SimpleSecsLogger(s => LogReceived?.Invoke(
                new SecsLogItem { Direction = LogDirection.Info, Content = s }));

            // 创建 HSMS 连接对象（此时还没有真正开始监听）。
            _connection = new HsmsConnection(options, _logger);

            // 创建 SecsGem 对象，它依赖 connection 做实际收发。
            _secsGem = new SecsGem(options, _connection, _logger);

            // 订阅连接状态变化事件（例如 Selected、Retry、Connecting 等）。
            // 这个回调可能在线程池线程上触发，所以这里只 Invoke 日志事件，
            // 不直接碰 UI。
            _connection.ConnectionChanged += (_, state) => LogReceived?.Invoke(
                new SecsLogItem { Direction = LogDirection.Info, Content = $"连接状态: {state}" });

            // 1. 启动 Socket 监听。
            // secs4net v3.0+ 的 Start 是同步方法（内部启动监听线程/任务），很快返回。
            // 传入 CancellationToken.None 表示“不通过这个 token 取消”，
            // 停止监听靠后面的 DisposeAsync 释放 connection。
            _connection.Start(CancellationToken.None);

            // 2. 在后台任务里消费“主消息流”。
            // GetPrimaryMessageAsync 返回 IAsyncEnumerable，会持续产出 Host 发来的
            // Primary Message（需要回复的消息）。必须在后台线程消费，不能阻塞 UI。
            _receiveCts = new CancellationTokenSource();
            var token = _receiveCts.Token;
            Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in _secsGem.GetPrimaryMessageAsync(token))
                    {
                        // e.PrimaryMessage 是收到的消息对象，用完必须 Dispose（它持有非托管缓冲）。
                        // 用 using 确保每条消息处理完就释放。
                        using var primary = e.PrimaryMessage;

                        // 路由到具体的处理逻辑（switch）。
                        await ProcessPrimaryMessageAsync(e, primary);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 正常取消（DisposeAsync 里 Cancel），不当作错误。
                }
                catch (Exception ex)
                {
                    // 接收循环本身出错（例如连接异常断开），记录错误日志。
                    // 注意：这里不重新抛出，避免 Task 变成 faulted 而无人观察。
                    LogReceived?.Invoke(new SecsLogItem { Direction = LogDirection.Error, Content = $"接收流异常: {ex.Message}" });
                }
            }, token);
        }

        /// <summary>
        /// 处理一条 Host 发来的 Primary Message。
        /// 
        /// 当前只处理 S1F1 和 S1F13。后续应按“消息族”拆到 Handler 类里，
        /// 这里只保留 switch 路由，避免本方法变成几千行的上帝方法。
        /// </summary>
        private async Task ProcessPrimaryMessageAsync(PrimaryMessageWrapper e, SecsMessage primary)
        {
            try
            {
                // 先把“收到消息”记入日志，方便在 UI 上观察交互时序。
                // Content 用 SecsItem.ToString() 得到 SML 风格文本；如果消息没有数据体则为空串。
                LogReceived?.Invoke(new SecsLogItem
                {
                    Direction = LogDirection.Receive,
                    MessageText = $"S{primary.S}F{primary.F}",
                    Content = primary.SecsItem?.ToString() ?? ""
                });

                switch (primary.S, primary.F)
                {
                    case (1, 1): // Are You There：Host 询问设备是否在线
                        // 回复 S1F2，数据体是 L(MDLN, SOFTREV)：
                        // MDLN = 设备型号，SOFTREV = 软件版本。
                        // 这里用 ASCII 字符串 "EQP" 和 "1.0.0" 示意，实际按设备真实信息填。
                        await ReplyAsync(e, 1, 2, L(A("EQP"), A("1.0.0")));
                        break;

                    case (1, 13): // Establish Communication：Host 请求建立通信
                        // 回复 S1F14，标准结构是 L(COMMACK, L(MDLN, SOFTREV))：
                        // COMMACK 是 B 类型，B(0) 表示接受通信（非 0 表示拒绝）。
                        // 注意：MDLN/SOFTREV 要嵌套在第二个 L 里，不能平铺在第一层。
                        await ReplyAsync(e, 1, 14, L(B(0), A("EQP"), A("1.0.0")));
                        break;

                        // 后续在这里补：
                        // case (1, 15): // Request OFF-LINE → 回 S1F16(OFLACK)
                        // case (1, 17): // Request ON-LINE  → 回 S1F18(ONLACK)
                        // case (2, 41): // Host Command     → 回 S2F42(HCACK)
                        // case (7, 1):  // 配方上传请求     → 回 S7F2
                        // case (7, 3):  // 配方下载请求     → 回 S7F4
                        // case (7, 5):  // 配方查询请求     → 回 S7F6
                        // case (6, 23): // Trace 启停       → 回 S6F24
                        // case (5, 3):  // 报警启停         → 回 S5F4
                }
            }
            catch (Exception ex)
            {
                // 单条消息处理失败不应影响整个接收循环，记录错误后继续处理下一条。
                LogReceived?.Invoke(new SecsLogItem
                {
                    Direction = LogDirection.Error,
                    Content = $"处理 S{primary.S}F{primary.F} 失败: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 发送回复消息的辅助方法。
        /// 
        /// 所有 Handler 都通过它回复，保证：
        /// 1. 回复消息被正确 Dispose（using）。
        /// 2. 发送前统一记录“发出”日志。
        /// 3. 用 TryReplyAsync 而不是 SendAsync：TryReplyAsync 是“对某条 Primary Message
        ///    的回复”，它内部会正确处理事务/会话；SendAsync 用于主动发起新事务。
        /// </summary>
        internal async Task ReplyAsync(PrimaryMessageWrapper wrapper, byte s, byte f, Item item)
        {
            using var reply = new SecsMessage(s, f) { SecsItem = item };

            LogReceived?.Invoke(new SecsLogItem
            {
                Direction = LogDirection.Send,
                MessageText = $"S{s}F{f}",
                Content = item.ToString() ?? ""
            });

            // ConfigureAwait(false) 表示“不需要回到原来的 SynchronizationContext”。
            // 因为这是后台线程调用的，不涉及 UI，加上可以避免不必要的线程切换开销。
            await wrapper.TryReplyAsync(reply).ConfigureAwait(false);
        }

        /// <summary>
        /// 主动上报事件报告 S6F11（设备 → Host）。
        /// 
        /// S6F11 标准结构：L(DATAID, CEID, L(L(RPTID, L(V1, V2, ...)) ...))
        /// - DATAID：U4，通常填 0
        /// - CEID：U2，事件 ID
        /// - 报告列表：每个报告是 L(RPTID, L(变量值...))
        /// 
        /// 注意：这里用 SendAsync 而不是 TryReplyAsync，因为这是设备主动发起的新事务，
        /// 需要等待 Host 回复 S6F12。
        /// </summary>
        public async Task ReportEventAsync(ushort ceid, uint rptid, params Item[] data)
        {
            if (_secsGem is null) throw new InvalidOperationException("连接未启动");

            var s6f11 = new SecsMessage(6, 11)
            {
                // 组包：L(DATAID, CEID, L(L(RPTID, L(data))))
                // data 是 params Item[]，用 L(data) 包成变量值列表。
                SecsItem = L(U4(0), U2(ceid), L(L(U4(rptid), L(data))))
            };

            LogReceived?.Invoke(new SecsLogItem
            {
                Direction = LogDirection.Send,
                MessageText = "S6F11",
                Content = $"CEID={ceid}, RPTID={rptid}, 数据={string.Join<Item>(", ", data)}"
            });

            try
            {
                // SendAsync 会阻塞等待 Host 的回复（S6F12），直到 T3 超时。
                using var reply = await _secsGem.SendAsync(s6f11).ConfigureAwait(false);

                LogReceived?.Invoke(new SecsLogItem
                {
                    Direction = LogDirection.Receive,
                    MessageText = $"S{reply.S}F{reply.F}",
                    Content = reply.SecsItem?.ToString() ?? ""
                });

                // 可选：校验回复是否真的是 S6F12。如果不是（例如 S6F0 中止），
                // 可以在这里额外记一条 Error 日志，方便排查 Host 是否接受了上报。
            }
            catch (SecsException ex)
            {
                // SecsException 封装了 T3 超时、收到 SxF0 中止、S9Fx 系统错误等典型场景。
                LogReceived?.Invoke(new SecsLogItem
                {
                    Direction = LogDirection.Error,
                    MessageText = "S6F11",
                    Content = $"上报失败: {ex.Message}"
                });
            }
        }

        /// <summary>
        /// 释放资源：停止接收循环、关闭 HSMS 连接。
        /// 
        /// 调用顺序很重要：
        /// 1. 先 Cancel 接收循环，让 await foreach 退出。
        /// 2. 再 Dispose connection，关闭 socket。
        /// 
        /// 注意：当前实现没有 await 接收 Task，理论上存在“connection 已释放、
        /// 接收循环还在跑”的短暂窗口。更严格的做法是把 Task.Run 的返回值存到字段，
        /// 在这里 await 它退出后再 Dispose connection。当前设备端消息量小，风险低。
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();

            if (_connection is not null)
                await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ISecsGemLogger 的最简实现：把 secs4net 内部日志转成一个字符串回调。
    /// 
    /// secs4net 要求调用方提供这个接口，用于输出通信日志、警告和错误。
    /// 我们不需要复杂的日志框架，只需要把字符串交给 SecsEquipmentService，
    /// 再由它包装成 SecsLogItem 抛给 UI。
    /// </summary>
    public class SimpleSecsLogger : ISecsGemLogger
    {
        private readonly Action<string> _sink;

        public SimpleSecsLogger(Action<string> sink) => _sink = sink;

        /// <summary>每条 SECS 消息收发时触发（描述 + SxFy）。</summary>
        public void Message(SecsMessage msg, string description) => _sink($"{description}: S{msg.S}F{msg.F}");

        /// <summary>普通信息日志。</summary>
        public void Info(string msg) => _sink(msg);

        /// <summary>警告日志（例如超时重试、连接异常但可恢复）。</summary>
        public void Warning(string msg) => _sink("警告: " + msg);

        /// <summary>错误日志（例如连接失败、协议错误）。</summary>
        public void Error(string msg, Exception? ex = null) => _sink("错误: " + msg + " " + ex?.Message);
    }
}
