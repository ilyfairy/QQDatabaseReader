# QQDatabaseReader

QQ / QQNT 本地聊天数据库读取, 浏览和导出工具.

## 支持范围

- Windows QQNT
  - 支持 `nt_msg.db`
  - 支持 `group_info.db`
  - 支持 `profile_info.db`
  - 支持 `group_msg_fts.db` 群消息搜索
  - 支持配置 `nt_data` 目录, 用于读取本地图片和图片表情
- Android QQNT
  - 支持 Android QQNT 架构的 `nt_msg.db`
  - 支持配套的 `group_info.db`, `profile_info.db`, `group_msg_fts.db`
  - 支持配置 `MobileQQ` 目录, 用于读取本地图片
  - 支持通过 `NtUid` + `Rand` 生成数据库密钥
- PCQQ
  - 支持 `Msg3.0.db`
  - 支持可选的 `Info.db`, 用于读取真实群号, 群名和联系人资料
  - 支持配置当前 QQ 号的数据目录, 用于读取本地图片
  - 支持登录 PCQQ 自动获取 `Msg3.0.db` 和 `Info.db` 密钥

## 数据库文件

### Windows QQNT

- `nt_msg.db`: 消息数据库
- `group_info.db`: 群资料和群成员资料
- `profile_info.db`: 好友, 用户昵称和备注资料
- `group_msg_fts.db`: 群消息全文搜索数据库
- `nt_data`: 本地图片, 文件和表情资源目录

数据库密钥可以通过 `QQDatabaseKeyDump` 自动获取, 也可以手动用调试器获取.

手动找 QQNT Windows 数据库密钥:

- 用 x64dbg 打开一个新的 QQ 进程, 例如 `C:\Program Files\Tencent\QQNT\QQ.exe`, 或者附加进程, 进入 QQ 登录界面.
- 等模块列表里出现 `wrapper.node`.
- 转到 CPU, 右键选择 `搜索 -> 所有用户模块 -> 字符串`, 搜索 `nt_sqlite3_key_v2`.
- 找到 `nt_sqlite3_key_v2: db=%p zDb=%s`, 按 F2 设置断点.
- 在 QQ 登录界面登录, 触发断点.
- 触发断点后, `rdx` 通常是 `main` 字符串, `r8` 是数据库密钥地址.
- 右键 `r8` 寄存器并转到内存窗口, 可以看到 16 字节 ASCII 密钥.

![设置断点](./x64dbg_qqnt.png)

![断点触发](./x64dbg_qqnt2.png)

### Android QQNT

- `nt_msg.db`: 消息数据库
- `group_info.db`: 群资料和群成员资料
- `profile_info.db`: 好友, 用户昵称和备注资料
- `group_msg_fts.db`: 群消息全文搜索数据库
- `MobileQQ`: 从手机目录备份出的 QQ 数据目录, 用于读取本地图片

Android QQNT 的 `nt_msg.db` 密钥由 `NtUid` 和 `Rand` 计算得到. 打开 AndroidQQNT 数据库时:

- 填写 `nt_msg.db` 路径.
- 填写 `NtUid`.
- `Rand` 可以从数据库头部自动读取; 如果读取失败, 可以手动填写.
- 程序会根据 `NtUid` + `Rand` 自动计算 `nt_msg.db` 密钥.
- 如果有 `group_info.db`, `profile_info.db`, `group_msg_fts.db`, 可以一起填写.
- 如果需要显示本地图片, 填写 `MobileQQ` 目录.

`MobileQQ` 是 Android 设备中备份出的 QQ 数据目录, 比如:

- `/sdcard/tencent/MobileQQ`
- `/storage/emulated/0/Android/data/com.tencent.mobileqq/Tencent/MobileQQ`

### PCQQ

- `Msg3.0.db`: 消息数据库
- `Info.db`: 联系人, 群资料等信息
- 数据目录: 当前 QQ 号的数据目录, 用于读取本地图片等媒体文件

打开 PCQQ 数据库时:

- `Msg3.0.db` 必填.
- `Msg3.0.db` 密钥必填.
- `Info.db` 可选; 填写后可以显示更准确的群号, 群名和联系人资料.
- `Info.db` 使用独立密钥, 和 `Msg3.0.db` 密钥不同.
- 数据目录可选; 填写后可以解析本地图片路径.

可以在桌面程序里使用 `登录 PCQQ 自动获取 Key` 获取 PCQQ 数据库密钥. 登录 PCQQ 后, 窗口里会显示匹配到的 `Msg3.0.db` / `Info.db` 路径和对应密钥.

## 鸣谢
- https://github.com/mobyw/GroupChatAnnualReport
- https://cyp0633.icu/post/android-qqnt-export
- https://github.com/artiga033/ntdb_unwrap
- https://docs.aaqwq.top/view/db_file_analysis/
- https://blog.reincarnatey.net/2024/0707-qqnt-history-export/
- https://github.com/QQBackup/qq-win-db-key
- https://github.com/QQBackup/QQ-History-Backup
- https://github.com/Akegarasu/qmsg-unpacker
