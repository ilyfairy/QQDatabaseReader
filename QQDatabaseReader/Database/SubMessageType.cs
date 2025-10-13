namespace QQDatabaseReader.Database;

/// <summary>
/// 疑似用于区分 Protobuf 消息的子类型 <br/>
/// 注意：存在多个枚举成员拥有相同的值，它们代表完全不同的消息类型。 <br/>
/// 在使用时需要特别注意上下文，或检查是否存在其他字段用于区分。 <br/>
/// https://docs.aaqwq.top/view/db_file_analysis/nt_msg.db.html#_40012%E9%83%A8%E5%88%86%E5%80%BC%E4%BF%A1%E6%81%AF%E8%A7%A3%E8%AF%BB
/// </summary>
public enum SubMessageType : int
{
    /// <summary>
    /// 非常规text消息：0
    /// </summary>
    SpecialText = 0,

    /// <summary>
    /// 普通文本消息：1
    /// </summary>
    Text = 1,

    /// <summary>
    /// (别名) 群文件其他类型消息：1
    /// </summary>
    GroupFileOther = 1,

    /// <summary>
    /// 图片消息：2
    /// </summary>
    Image = 2,

    /// <summary>
    /// (别名) 群文件图片消息：2
    /// </summary>
    GroupFileImage = 2,

    /// <summary>
    /// 群公告：3
    /// </summary>
    GroupAnnouncement = 3,

    /// <summary>
    /// 群文件视频消息：4
    /// </summary>
    GroupFileVideo = 4,

    /// <summary>
    /// (冲突) 撤回消息提醒：4
    /// </summary>
    MessageRecalled = 4,

    /// <summary>
    /// 群文件音频消息：8
    /// </summary>
    GroupFileAudio = 8,

    /// <summary>
    /// (冲突) 原创表情包：8
    /// </summary>
    OriginalSticker = 8,

    /// <summary>
    /// 特殊互动消息（如戳一戳/窗口抖动等效果）：11
    /// </summary>
    /// <remarks>
    /// 原文“射精消息”应为俚语，指代某种快速、连续的互动效果。
    /// </remarks>
    Nudge = 11,

    /// <summary>
    /// 拍一拍消息：12
    /// </summary>
    Pat = 12,

    /// <summary>
    /// 群文件docx消息：16
    /// </summary>
    GroupFileDocx = 16,

    /// <summary>
    /// 平台文本消息：32
    /// </summary>
    PlatformText = 32,

    /// <summary>
    /// (冲突) 群文件pptx消息：32
    /// </summary>
    GroupFilePptx = 32,

    /// <summary>
    /// 回复类型消息：33 (可能是对某条消息的文本回复的子类型)
    /// </summary>
    Reply = 33,

    /// <summary>
    /// 群文件xlsx消息：64
    /// </summary>
    GroupFileXlsx = 64,

    /// <summary>
    /// 消息内容中存在链接：161
    /// </summary>
    ContainsLink = 161,

    /// <summary>
    /// 群文件zip消息：512
    /// </summary>
    GroupFileZip = 512,

    /// <summary>
    /// 群文件exe消息：2048
    /// </summary>
    GroupFileExe = 2048,

    /// <summary>
    /// (普通)表情消息：4096
    /// </summary>
    Sticker = 4096
}
