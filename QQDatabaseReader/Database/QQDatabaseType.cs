
namespace QQDatabaseReader;

public enum QQDatabaseType
{
    /// <summary>
    /// nt_msg.db
    /// </summary>
    Message,

    /// <summary>
    /// Android QQNT nt_msg.db
    /// </summary>
    AndroidMessage,

    /// <summary>
    /// group_info.db
    /// </summary>
    GroupInfo,

    /// <summary>
    /// group_msg_fts.db
    /// </summary>
    GroupMessageFts,

    /// <summary>
    /// profile_info.db
    /// </summary>
    ProfileInfo,

    /// <summary>
    /// PCQQ Msg3.0.db
    /// </summary>
    PCQQMessage,

    /// <summary>
    /// Icalingua-plus-plus eqq*.db
    /// </summary>
    IcalinguaMessage,

    /// <summary>
    /// Legacy Android QQ MobileQQ message database
    /// </summary>
    AndroidMobileQQMessage,
}
