using SQLitePCL;

namespace QQDatabaseReader.Database;

/// <summary>
/// SQL 函数 qqdbr_uin(nt_uid): 把 nt_uid 换成 QQ 号, 查不到为 0.
/// 旧版 QQNT 的消息表没有 QQ 号列 (40033/40030), 兼容映射用它从 nt_uid 补出来
/// </summary>
public static class QQNtUinFunction
{
    public const string Name = "qqdbr_uin";

    /// <summary>每次调用时取当前的解析器, 资料库后加载或被移除都能生效</summary>
    internal static void Register(sqlite3 database, Func<Func<string, long>?> getResolver)
    {
        var rc = raw.sqlite3_create_function(database, Name, 1, raw.SQLITE_UTF8, null, (context, _, args) =>
        {
            long uin = 0;
            var resolver = getResolver();
            if (resolver is not null && raw.sqlite3_value_type(args[0]) == raw.SQLITE_TEXT)
            {
                var uid = raw.sqlite3_value_text(args[0]).utf8_to_string();
                try
                {
                    if (!string.IsNullOrEmpty(uid))
                        uin = resolver(uid);
                }
                catch
                {
                    // A broken lookup must not fail the whole message query
                }
            }

            raw.sqlite3_result_int64(context, uin);
        });
        if (rc != raw.SQLITE_OK)
            throw new InvalidOperationException($"注册 SQL 函数 {Name} 失败: {raw.sqlite3_errmsg(database).utf8_to_string()}");
    }
}
