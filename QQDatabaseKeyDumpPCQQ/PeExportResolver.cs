using System.Text;

namespace QQDatabaseKeyDumpPCQQ;

public static class PeExportResolver
{
    public static uint? GetExportRva(string dllPath, string exportName)
    {
        try
        {
            byte[] file = File.ReadAllBytes(dllPath);
            int peOffset = BitConverter.ToInt32(file, 0x3C);
            if (peOffset <= 0 ||
                peOffset + 0x18 >= file.Length ||
                BitConverter.ToUInt32(file, peOffset) != 0x4550)
            {
                return null;
            }

            ushort optionalHeaderSize = BitConverter.ToUInt16(file, peOffset + 0x14);
            int optionalHeaderOffset = peOffset + 0x18;
            ushort magic = BitConverter.ToUInt16(file, optionalHeaderOffset);
            int dataDirectoryOffset = optionalHeaderOffset + (magic == 0x20B ? 112 : 96);
            uint exportRva = BitConverter.ToUInt32(file, dataDirectoryOffset);
            if (exportRva == 0)
                return null;

            ushort sectionCount = BitConverter.ToUInt16(file, peOffset + 6);
            var sections = new (uint VirtualAddress, uint VirtualSize, uint RawAddress)[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                int offset = optionalHeaderOffset + optionalHeaderSize + 40 * i;
                sections[i] = (
                    BitConverter.ToUInt32(file, offset + 12),
                    BitConverter.ToUInt32(file, offset + 8),
                    BitConverter.ToUInt32(file, offset + 20));
            }

            int RvaToOffset(uint rva)
            {
                foreach (var (virtualAddress, virtualSize, rawAddress) in sections)
                {
                    if (rva >= virtualAddress && rva < virtualAddress + Math.Max(virtualSize, 1u))
                        return (int)(rawAddress + (rva - virtualAddress));
                }

                return -1;
            }

            int exportOffset = RvaToOffset(exportRva);
            if (exportOffset < 0 || exportOffset + 0x28 > file.Length)
                return null;

            uint nameCount = BitConverter.ToUInt32(file, exportOffset + 0x18);
            uint functionAddressTable = BitConverter.ToUInt32(file, exportOffset + 0x1C);
            uint nameAddressTable = BitConverter.ToUInt32(file, exportOffset + 0x20);
            uint ordinalAddressTable = BitConverter.ToUInt32(file, exportOffset + 0x24);
            int functionOffset = RvaToOffset(functionAddressTable);
            int nameOffset = RvaToOffset(nameAddressTable);
            int ordinalOffset = RvaToOffset(ordinalAddressTable);
            if (functionOffset < 0 || nameOffset < 0 || ordinalOffset < 0)
                return null;

            for (int i = 0; i < nameCount; i++)
            {
                uint nameRva = BitConverter.ToUInt32(file, nameOffset + 4 * i);
                int stringOffset = RvaToOffset(nameRva);
                if (stringOffset < 0)
                    continue;

                int len = 0;
                while (stringOffset + len < file.Length && file[stringOffset + len] != 0)
                    len++;

                string name = Encoding.ASCII.GetString(file, stringOffset, len);
                if (name == exportName)
                {
                    ushort ordinal = BitConverter.ToUInt16(file, ordinalOffset + 2 * i);
                    return BitConverter.ToUInt32(file, functionOffset + 4 * ordinal);
                }
            }
        }
        catch
        {
        }

        return null;
    }
}
