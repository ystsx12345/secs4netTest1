using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace secs4netTest1
{
    public enum LogDirection { Info, Receive, Send, Error }

    public class SecsLogItem
    {
        public DateTime Time { get; init; } = DateTime.Now;
        public LogDirection Direction { get; init; }
        public string MessageText { get; init; } = "";
        public string Content { get; init; } = "";

        public string TimeText => Time.ToString("HH:mm:ss.fff");
        public string DirectionText => Direction switch
        {
            LogDirection.Receive => "◀ 收",
            LogDirection.Send => "▶ 发",
            LogDirection.Error => "错误",
            _ => "信息"
        };
    }
}
