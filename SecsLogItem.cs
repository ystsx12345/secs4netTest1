using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace secs4netTest1
{
    /// <summary>
    /// 日志方向枚举：用于在 UI 上区分一条日志是“系统信息 / 收到报文 / 发出报文 / 错误”。
    /// 之所以用枚举而不是字符串，是为了在 XAML 的 DataTrigger 里能按值精确匹配来着色，
    /// 同时避免手写字符串拼错（编译期就能检查）。
    /// </summary>
    public enum LogDirection { Info, Receive, Send, Error }

    /// <summary>
    /// 一条 SECS 交互日志的 UI 数据模型。
    /// 它不参与任何协议通信，只是把后台线程产生的日志“打包”成对象，
    /// 通过 event 快递到 UI 线程后，直接绑定到 ListView 显示。
    /// 
    /// 注意：这个类被设计成“不可变”的（属性全是 init），
    /// 因为日志一旦产生就不应再被修改，避免 UI 绑定过程中出现数据竞争。
    /// </summary>
    public class SecsLogItem
    {
        /// <summary>日志产生的时间，默认取当前时间。用于 UI 上按时间排序/查看。</summary>
        public DateTime Time { get; init; } = DateTime.Now;

        /// <summary>日志方向：信息/收/发/错误。决定 UI 上的颜色和文字。</summary>
        public LogDirection Direction { get; init; }

        /// <summary>
        /// 消息标识，例如 "S1F1"、"S6F11"。
        /// 对于 Info/Error 类日志可能为空字符串，属于正常情况。
        /// </summary>
        public string MessageText { get; init; } = "";

        /// <summary>
        /// 消息体或日志正文。
        /// 对于 SECS 报文，这里通常是 SecsItem.ToString() 的结果（SML 风格文本）；
        /// 对于 Info/Error，这里就是人类可读的描述文字。
        /// </summary>
        public string Content { get; init; } = "";

        /// <summary>
        /// 给 UI 直接绑定的时间文本（HH:mm:ss.fff）。
        /// 用计算属性而不是在构造时格式化，是为了让 Time 保持 DateTime 类型，
        /// 将来若需要按时间过滤/排序仍可用原始值，同时 UI 又不用写 Converter。
        /// </summary>
        public string TimeText => Time.ToString("HH:mm:ss.fff");

        /// <summary>
        /// 给 UI 直接绑定的方向文本。
        /// 用 switch 表达式把枚举映射成带符号的中文，视觉上一眼能区分收/发。
        /// 默认分支（_）覆盖 Info，返回“信息”。
        /// </summary>
        public string DirectionText => Direction switch
        {
            LogDirection.Receive => "◀ 收",
            LogDirection.Send => "▶ 发",
            LogDirection.Error => "错误",
            _ => "信息"
        };
    }
}
