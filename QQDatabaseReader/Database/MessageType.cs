
namespace QQDatabaseReader.Database;

/// <summary>
/// 消息类型 <br/>
/// https://docs.aaqwq.top/view/db_file_analysis/nt_msg.db.html#_40011%E9%83%A8%E5%88%86%E5%80%BC%E4%BF%A1%E6%81%AF%E8%A7%A3%E8%AF%BB
/// </summary>
public enum MessageType : int
{
    /// <summary>
    /// 无消息（消息损坏？多见于已退出群聊且时间久远）
    /// </summary>
    None = 0,

    /// <summary>
    /// 消息空白（msgid存在，应该是没加载出来）
    /// </summary>
    Empty = 1,

    /// <summary>
    /// 文本消息
    /// </summary>
    Text = 2,

    /// <summary>
    /// 群文件
    /// </summary>
    GroupFile = 3,

    // 4: 未知

    /// <summary>
    /// 系统（灰字）消息
    /// </summary>
    System = 5,

    /// <summary>
    /// 语音消息
    /// </summary>
    Voice = 6,

    /// <summary>
    /// 视频文件
    /// </summary>
    Video = 7,

    /// <summary>
    /// 合并转发消息
    /// </summary>
    Forwarded = 8,

    /// <summary>
    /// 回复类型消息
    /// </summary>
    Reply = 9,

    /// <summary>
    /// 红包
    /// </summary>
    RedPacket = 10,

    /// <summary>
    /// 应用消息 (例如小程序、分享链接等)
    /// </summary>
    App = 11
}
