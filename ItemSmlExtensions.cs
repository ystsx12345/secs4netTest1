using Secs4Net;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace secs4netTest1
{
    public static class ItemSmlExtensions
    {
        /// <summary>
        /// 将 Secs4Net 的 Item 对象转换为标准的 SML（SECS Message Language）字符串。
        /// </summary>
        /// <param name="item">要转换的 Item</param>
        /// <param name="indentLevel">当前缩进级别（内部递归使用）</param>
        /// <returns>带缩进和完整数据的 SML 字符串</returns>
        public static string ToSmlString(this Item? item, int indentLevel = 0)
        {
            if (item == null) return string.Empty;

            // 基础缩进（每个层级 4 个空格）
            string indent = new string(' ', indentLevel * 4);
            var sb = new StringBuilder();

            // 1. List 类型 (L) 需要特殊处理，因为它包含子节点
            if (item.Format == SecsFormat.List)
            {
                // 如果 List 为空
                if (item.Count == 0)
                {
                    sb.AppendLine($"{indent}<L [0]>");
                    return sb.ToString();
                }

                sb.AppendLine($"{indent}<L [{item.Count}]");
             
                // 递归遍历所有子 Item
                foreach (var child in item.Items)
                {
                    sb.Append(child.ToSmlString(indentLevel + 1));
                }

                sb.AppendLine($"{indent}>");
            }
            // 2. 其他基本类型（无子节点的）
            else
            {
                // 获取类型名称（如 U4, A, B）
                string formatName = GetFormatName(item.Format);

                // 如果是二进制 B 类型，多个字节需要用逗号隔开
                if (item.Format == SecsFormat.Binary)
                {
                    var bytes = item.GetMemory<byte>().ToArray();
                    string hexStr = string.Join(" ", bytes.Select(b => $"0x{b:X2}"));
                    sb.AppendLine($"{indent}<B [{item.Count}] {hexStr}>");
                }
                else
                {
                    // 获取值的字符串表示
                    string valueStr = GetValueString(item);
                    // 标准 SML 格式：<Type [Count] 'Value'>
                    sb.AppendLine($"{indent}<{formatName} [{item.Count}] '{valueStr}'>");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// 将 SecsFormat 枚举转为 SML 标准的大写类型字符串
        /// </summary>
        private static string GetFormatName(SecsFormat format)
        {
            return format switch
            {
                SecsFormat.List => "L",
                SecsFormat.Binary => "B",
                SecsFormat.Boolean => "BOOLEAN",
                SecsFormat.ASCII => "A",
                SecsFormat.JIS8 => "J",
                SecsFormat.I8 => "I8",
                SecsFormat.I1 => "I1",
                SecsFormat.I2 => "I2",
                SecsFormat.I4 => "I4",
                SecsFormat.F8 => "F8",
                SecsFormat.F4 => "F4",
                SecsFormat.U8 => "U8",
                SecsFormat.U1 => "U1",
                SecsFormat.U2 => "U2",
                SecsFormat.U4 => "U4",
                _ => format.ToString().ToUpper()
            };
        }

        /// <summary>
        /// 提取基本类型的值字符串
        /// </summary>
        private static string GetValueString(Item item)
        {
            try
            {
                return item.Format switch
                {
                    SecsFormat.ASCII => item.GetString(),
                    SecsFormat.JIS8 => item.GetString(),
                    // 对于数字类型，获取第一个值并转为字符串（若需处理数组，可进一步循环）
                    SecsFormat.Boolean => item.FirstValue<bool>() ? "True" : "False",
                    SecsFormat.I1 => item.FirstValue<sbyte>().ToString(),
                    SecsFormat.I2 => item.FirstValue<short>().ToString(),
                    SecsFormat.I4 => item.FirstValue<int>().ToString(),
                    SecsFormat.I8 => item.FirstValue<long>().ToString(),
                    SecsFormat.U1 => item.FirstValue<byte>().ToString(),
                    SecsFormat.U2 => item.FirstValue<ushort>().ToString(),
                    SecsFormat.U4 => item.FirstValue<uint>().ToString(),
                    SecsFormat.U8 => item.FirstValue<ulong>().ToString(),
                    SecsFormat.F4 => item.FirstValue<float>().ToString("G"),
                    SecsFormat.F8 => item.FirstValue<double>().ToString("G"),
                    _ => item.ToString() ?? ""
                };
            }
            catch
            {
                // 防御性编程：如果取值失败，回退到普通的 ToString
                return item.ToString() ?? "";
            }
        }
    }
}
