namespace QQDatabaseKeyDump;

public static class QQDatabaseKeyHelpers
{
    public static int FindLinuxFunctionOffset()
    {
        // lea rsi, aNtSqlite3KeyV2 ; "nt_sqlite3_key_v2: db=%p zDb=%s"
        return 0;
    }

    //public static int FindWindowsFunctionOffset(string wrapperNodeFilePath)
    //{
        
    //}

    //private List<NativeDebugger.SectionInfo> ReadSections(IntPtr moduleBase)
    //{
    //    var sections = new List<NativeDebugger.SectionInfo>();
    //    ulong baseAddr = (ulong)moduleBase;

    //    uint e_lfanew = ReadUInt32(baseAddr + 0x3C);
    //    ulong ntHeader = baseAddr + e_lfanew;
    //    uint signature = ReadUInt32(ntHeader + 0);
    //    if (signature != 0x00004550)
    //        return sections;

    //    ushort numberOfSections = ReadUInt16(ntHeader + 4 + 2);
    //    ushort sizeOfOptionalHeader = ReadUInt16(ntHeader + 4 + 16);

    //    ulong sectionHeaders = ntHeader + 4 + 20 + sizeOfOptionalHeader;
    //    for (int i = 0; i < numberOfSections; i++)
    //    {
    //        ulong sh = sectionHeaders + (ulong)i * 40UL;
    //        string name = ReadAsciiFixed(sh + 0, 8).TrimEnd('\0');
    //        uint virtualSize = ReadUInt32(sh + 8);
    //        uint virtualAddress = ReadUInt32(sh + 12);

    //        sections.Add(new NativeDebugger.SectionInfo
    //        {
    //            Name = name,
    //            VirtualSize = virtualSize,
    //            VirtualAddress = virtualAddress,
    //            StartAddress = baseAddr + virtualAddress
    //        });
    //    }

    //    return sections;
    //}
}
