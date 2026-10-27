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
    public class SecsEquipmentService : IAsyncDisposable
    {
        private SecsGem? _secsGem;
        private HsmsConnection? _connection;
        private SimpleSecsLogger? _logger;
        private CancellationTokenSource? _receiveCts;

        // ⬇️ 这里就是那个“快递员”事件，定义在类里面，完全合法且标准
        public event Action<SecsLogItem>? LogReceived;

        public void Start(string ip, int port, ushort deviceId)
        {
            var options = Options.Create(new SecsGemOptions
            {
                DeviceId = deviceId,
                IsActive = false,        // 被动监听
                IpAddress = ip,
                Port = port,
                T3 = 45000,
                T5 = 10000,
                T6 = 5000,
                T7 = 10000,
                T8 = 5000
            });

            _logger = new SimpleSecsLogger(s => LogReceived?.Invoke(
                new SecsLogItem { Direction = LogDirection.Info, Content = s }));

            _connection = new HsmsConnection(options, _logger);
            _secsGem = new SecsGem(options, _connection, _logger);

            _connection.ConnectionChanged += (_, state) => LogReceived?.Invoke(
                new SecsLogItem { Direction = LogDirection.Info, Content = $"连接状态: {state}" });

            // 1. 启动 Socket 监听 (v3.0+ 是同步方法)
            _connection.Start(CancellationToken.None);

            // 2. 在后台死循环里接收消息
            _receiveCts = new CancellationTokenSource();
            var token = _receiveCts.Token;
            Task.Run(async () =>
            {
                try
                {
                    await foreach (var e in _secsGem.GetPrimaryMessageAsync(token))
                    {
                        using var primary = e.PrimaryMessage;
                        await ProcessPrimaryMessageAsync(e, primary);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    LogReceived?.Invoke(new SecsLogItem { Direction = LogDirection.Error, Content = $"接收流异常: {ex.Message}" });
                }
            }, token);
        }

        private async Task ProcessPrimaryMessageAsync(PrimaryMessageWrapper e, SecsMessage primary)
        {
            try
            {
                LogReceived?.Invoke(new SecsLogItem
                {
                    Direction = LogDirection.Receive,
                    MessageText = $"S{primary.S}F{primary.F}",
                    Content = primary.SecsItem?.ToString() ?? ""
                });

                switch (primary.S, primary.F)
                {
                    case (1, 1): // Are You There
                        await ReplyAsync(e, 1, 2, L(A("EQP"), A("1.0.0")));
                        break;
                    case (1, 13): // Establish Communication
                        await ReplyAsync(e, 1, 14, L(B(0),A("EQP"), A("1.0.0")));
                        break;
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(new SecsLogItem
                {
                    Direction = LogDirection.Error,
                    Content = $"处理 S{primary.S}F{primary.F} 失败: {ex.Message}"
                });
            }
        }

        private async Task ReplyAsync(PrimaryMessageWrapper wrapper, byte s, byte f, Item item)
        {
            using var reply = new SecsMessage(s, f) { SecsItem = item };
            LogReceived?.Invoke(new SecsLogItem
            {
                Direction = LogDirection.Send,
                MessageText = $"S{s}F{f}",
                Content = item.ToString() ?? ""
            });
            await wrapper.TryReplyAsync(reply).ConfigureAwait(false);
        }

        public async Task ReportEventAsync(ushort ceid, uint rptid, params Item[] data)
        {
            if (_secsGem is null) throw new InvalidOperationException("连接未启动");

            var s6f11 = new SecsMessage(6, 11)
            {
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
                using var reply = await _secsGem.SendAsync(s6f11).ConfigureAwait(false);
                LogReceived?.Invoke(new SecsLogItem
                {
                    Direction = LogDirection.Receive,
                    MessageText = $"S{reply.S}F{reply.F}",
                    Content = reply.SecsItem?.ToString() ?? ""
                });
            }
            catch (SecsException ex)
            {
                LogReceived?.Invoke(new SecsLogItem
                {
                    Direction = LogDirection.Error,
                    MessageText = "S6F11",
                    Content = $"上报失败: {ex.Message}"
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public class SimpleSecsLogger : ISecsGemLogger
    {
        private readonly Action<string> _sink;
        public SimpleSecsLogger(Action<string> sink) => _sink = sink;

        public void Message(SecsMessage msg, string description) => _sink($"{description}: S{msg.S}F{msg.F}");
        public void Info(string msg) => _sink(msg);
        public void Warning(string msg) => _sink("警告: " + msg);
        public void Error(string msg, Exception? ex = null) => _sink("错误: " + msg + " " + ex?.Message);
    }
}
