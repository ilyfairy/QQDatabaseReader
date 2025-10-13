namespace QQDatabaseReader.Database;

public enum ChatType : int
{
    // 聊天类型，私聊为1，群聊为2，频道为4，公众号为103，企业客服为102，临时会话为100
    PrivateMessage = 1,
    GroupMessage = 2,
}
