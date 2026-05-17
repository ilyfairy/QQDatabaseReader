using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace QQDatabaseReader.Database;

/// <summary>
/// 私聊消息。QQNT 的 c2c_msg_table 使用 40027 作为本地会话 ID；
/// 40021 是 nt_uid，40030 是对方 QQ 号，不能把它们和群号混用。
/// </summary>
[Table("c2c_msg_table")]
public class PrivateMessage
{
    /// <summary>
    /// 消息内部 ID，主键。
    /// </summary>
    [Key]
    [Column("40001")]
    public long MessageId { get; set; }

    /// <summary>
    /// 消息随机值。QQNT 对私聊建了唯一索引 40027 + 40002 + 40005，
    /// 真实数据会超过 int 范围，必须用 long。
    /// </summary>
    [Column("40002")]
    public long MessageRandom { get; set; }

    /// <summary>
    /// 私聊消息序号，在同一会话内递增。
    /// </summary>
    [Column("40003")]
    public long MessageSeq { get; set; }

    /// <summary>
    /// 聊天类型。私聊通常为 1，临时会话/公众号等也可能存放在 c2c_msg_table。
    /// </summary>
    [Column("40010")]
    public ChatType ChatType { get; set; }

    [Column("40011")]
    public MessageType MessageType { get; set; }

    [Column("40012")]
    public SubMessageType SubMessageType { get; set; }

    /// <summary>
    /// 发送标志，本机发送通常为 1。
    /// </summary>
    [Column("40013")]
    public int SendType { get; set; }

    /// <summary>
    /// 发送者 nt_uid。
    /// </summary>
    [Column("40020")]
    public string SenderUid { get; set; } = string.Empty;

    /// <summary>
    /// 对方 nt_uid。
    /// </summary>
    [Column("40021")]
    public string PeerUid { get; set; } = string.Empty;

    /// <summary>
    /// QQNT 私聊本地会话 ID。分页加载必须走 40027 + 40003 索引。
    /// </summary>
    [Column("40027")]
    public long ConversationId { get; set; }

    [Column("40041")]
    public int SendStatus { get; set; }

    [Column("40050")]
    public int MessageTime { get; set; }

    [Column("40090")]
    public string? SendMemberName { get; set; }

    [Column("40093")]
    public string? SendNickName { get; set; }

    [Column("40800")]
    public byte[]? Content { get; set; }

    /// <summary>
    /// 转发消息缓存或引用消息缓存。
    /// </summary>
    [Column("40900")]
    public byte[]? SubContent { get; set; }

    [Column("40005")]
    public int Unknown0 { get; set; }

    /// <summary>
    /// 当日 0 时整的时间戳。
    /// </summary>
    [Column("40058")]
    public int DayTimestamp { get; set; }

    [Column("40006")]
    public int Unknown1 { get; set; }

    [Column("40100")]
    public int AtStatus { get; set; }

    [Column("40600")]
    public byte[]? StatusFlags { get; set; }

    [Column("40060")]
    public int InactiveConversationFlag { get; set; }

    /// <summary>
    /// 回复消息序号。
    /// </summary>
    [Column("40850")]
    public long ReplyToMessageSeq { get; set; }

    [Column("40801")]
    public byte[]? Unknown2 { get; set; }

    /// <summary>
    /// 对方 QQ 号。
    /// </summary>
    [Column("40030")]
    public uint PeerUin { get; set; }

    /// <summary>
    /// 发送者 QQ 号。
    /// </summary>
    [Column("40033")]
    public uint SenderId { get; set; }

    [Column("40062")]
    public byte[]? MessageReactions { get; set; }

    [Column("40083")]
    public int TotalReactionCount1 { get; set; }

    [Column("40084")]
    public int TotalReactionCount2 { get; set; }
}
