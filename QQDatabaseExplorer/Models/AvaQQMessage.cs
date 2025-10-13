using System;
using System.Collections.Generic;
using System.Text;

namespace QQDatabaseExplorer.Models;

public class AvaQQMessage
{
    public string DisplayText { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int MessageTime { get; set; }
    public long MessageId { get; set; }
    
    // 重写 Equals 和 GetHashCode 以支持 Dictionary 查找
    public override bool Equals(object? obj)
    {
        return obj is AvaQQMessage other && MessageId == other.MessageId;
    }
    
    public override int GetHashCode()
    {
        return MessageId.GetHashCode();
    }
}
