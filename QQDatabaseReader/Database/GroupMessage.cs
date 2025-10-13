using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;

namespace QQDatabaseReader.Database;

/// <summary>
/// 群消息 <br/>
/// https://docs.aaqwq.top/view/db_file_analysis/nt_msg.db.html
/// </summary>
[Table("group_msg_table")]
public class GroupMessage
{
    /// <summary>
    /// 消息ID，具有唯一性
    /// </summary>
    [Key]
    [Column("40001")]
    public int MessageId { get; set; }

    /// <summary>
    /// 消息随机值，用于对消息去重
    /// </summary>
    [Column("40002")]
    public int MessageRandom { get; set; }

    /// <summary>
    /// 群聊消息ID，在每个聊天中依次递增
    /// </summary>
    [Column("40003")]
    public int MessageSeq { get; set; }

    /// <summary>
    /// 聊天类型，私聊为1，群聊为2，频道为4，公众号为103，企业客服为102，临时会话为100
    /// </summary>
    [Column("40010")]
    public ChatType ChatType { get; set; }

    /// <summary>
    /// 消息类型
    /// </summary>
    [Column("40011")]
    public MessageType MessageType { get; set; }

    /// <summary>
    /// pb消息类型
    /// </summary>
    [Column("40012")]
    public SubMessageType SubMessageType { get; set; }

    /// <summary>
    /// 发送标志，本机发送的消息为1，其他客户端发送的为2，别人发的消息为0 ，转发消息为5，在已退出或被封禁的消息中为当日整点时间戳
    /// </summary>
    [Column("40013")]
    public int SendType { get; set; }

    /// <summary>
    /// nt_uid, 对应 nt_uid_mapping_table
    /// </summary>
    [Column("40020")]
    public string SenderUid { get; set; } = string.Empty;

    /// <summary>
    /// 会话ID
    /// </summary>
    [Column("40021")]
    public string PeerUid { get; set; } = string.Empty;

    /// <summary>
    /// 会话ID
    /// </summary>
    [Column("40027")]
    public int GroupId { get; set; }

    /// <summary>
    /// 发送状态，2为成功，0为发送被阻止（如不是对方好友），1为尚未发送成功（比如网络问题），3为消息被和谐
    /// </summary>
    [Column("40041")]
    public int SendStatus { get; set; }

    /// <summary>
    /// 发送消息时的完整时间戳，UTC+8：00
    /// </summary>
    [Column("40050")]
    public int MessageTime { get; set; }

    /// <summary>
    /// 发送者群名片，旧版 QQ 迁移数据中格式为 name(12345) 或 name<i@example.com>， QQNT 中为群名片
    /// </40090>
    [Column("40090")]
    public string? SendMemberName { get; set; }

    /// <summary>
    /// 发送者昵称，旧版 QQ 此字段为空，QQNT 中未设置群名片时才有此字段
    /// </summary>
    [Column("40093")]
    public string? SendNickName { get; set; }

    /// <summary>
    /// 聊天消息
    /// </summary>
    [Column("40800")]
    public byte[]? Content { get; set; }

    /// <summary>
    /// 值为8时，列40900存贮转发聊天的缓存 <br/>
    /// 值为9时，列40900存贮引用的消息
    /// </summary>
    [Column("40900")]
    public byte[]? SubContent { get; set; }

    /// <summary>
    /// 只知道自己发的消息一定概率存在数值, 正常情况为0
    /// </summary>
    [Column("40005")]
    public int Unknown0 { get; set; }

    /// <summary>
    /// 当日 0 时整的时间戳格式, 时区为 GMT+0800
    /// </summary>
    [Column("40058")]
    public int DayTimestamp { get; set; }
    
    [Column("40006")]
    public int Unknown1 { get; set; }

    /// <summary>
    /// @状态, 值为6：有人@我；为2，有人@他人；为0，此条消息不包含@
    /// </summary>
    [Column("40100")]
    public int AtStatus { get; set; }

    /// <summary>
    /// 当40600（16进制）值为14 00时，为回复消息
    /// </summary>
    [Column("40600")]
    public byte[]? StatusFlags { get; set; }

    /// <summary>
    /// 已退出或已解散的群聊标志
    /// </summary>
    [Column("40060")]
    public int InactiveGroupFlag { get; set; }

    /// <summary>
    /// 回复消息序号	该消息所回复的消息的序号
    /// </summary>
    [Column("40850")]
    public int ReplyToMessageSeq { get; set; }
    
    [Column("40801")]
    public byte[]? Unknown2 { get; set; }

    /// <summary>
    /// 群号
    /// </summary>
    [Column("40030")]
    public int SavedGroupId { get; set; }
    
    /// <summary>
    /// 发送者QQ号
    /// </summary>
    [Column("40033")]
    public int SenderId { get; set; }

    /// <summary>
    /// 存贮详细表态信息（包括表态表情和表态数量）, 其数字与QQBOT中表情编号对应（超级表情不在此列表中）
    /// </summary>
    [Column("40062")]
    public byte[]? MessageReactions { get; set; }

    [Column("40083")]
    public int TotalReactionCount1 { get; set; }

    [Column("40084")]
    public int TotalReactionCount2 { get; set; }





    public string GetText()
    {
        if(Content is null) return string.Empty;

        try
        {
            if (SubMessageType == SubMessageType.Image)
                return "[图片消息]";
            return QQMessageReader.ParseMessage(Content).GetText();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

[Table("recent_contact_v3_table")]
public class RecentContact
{
    [Column("40010")]
    public ChatType ChatType { get; set; }

    [Column("40021")]
    public string? PeerUin { get; set; }

    [Column("40030")]
    public int Uin { get; set; }

    [Column("40051")]
    public byte[]? LastMessage { get; set; }

    [Column("40041")]
    public int SendStatus { get; set; }

    [Column("40050")]
    public int LastTime { get; set; } // seconds

    [Column("40003")]
    public string? MessageSeq { get; set; }

    [Column("40094")]
    public string? Source { get; set; }

    [Column("40093")]
    public string? SendNickName { get; set; }

    [Column("40090")]
    public string? SendMemberName { get; set; }

    /// <summary>
    /// 成员备注名称
    /// </summary>
    [Column("40095")]
    public string? SendremarkName { get; set; }


    [Column("40020")]
    public string? NtUid { get; set; }


    [Column("40033")]
    public int Uin2 { get; set; }

    /// <summary>
    /// 群头像本地缓存路径
    /// </summary>
    [Column("41110")]
    public int GroupAvatar { get; set; }
    
    [Column("41135")]
    public string? _41135 { get; set; }

}