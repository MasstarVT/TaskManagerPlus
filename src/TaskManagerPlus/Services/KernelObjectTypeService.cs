using System.Runtime.InteropServices;
using TaskManagerPlus.Models;

namespace TaskManagerPlus.Services;

/// <summary>
/// #426: system-wide object counts per kernel object type (Event, Section, Token, File,
/// Semaphore, ...) via NtQueryObject(ObjectTypesInformation) - each type's own recorded
/// TotalNumberOfObjects/HighWaterMark, the closest thing Windows exposes to "which kind of kernel
/// object is piling up" without a kernel debugger attached.
///
/// This is the least stable struct layout in this app's whole native-interop surface: unlike
/// SYSTEM_POOLTAG (a fixed-size array of fixed-size structs) or SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX,
/// each OBJECT_TYPE_INFORMATION entry is followed immediately by its own variable-length name
/// buffer, then the next entry is realigned to a pointer boundary - a genuinely undocumented,
/// version-sensitive layout. Every offset computed while walking it is sanity-checked against the
/// buffer bounds, and the whole parse is wrapped so a bad read anywhere degrades to an empty list
/// (the Memory tab hides the section entirely) rather than misreading garbage as real counts -
/// this app's "never fabricate" rule applies just as much to a botched struct parse as it does to
/// a missing sensor. Run off the UI thread on an abandoned background thread with a strict
/// timeout, the same defensive shape HandleInspectionService.ResolveHandleType uses for
/// NtQueryObject calls that are known to occasionally hang.
/// </summary>
public static class KernelObjectTypeService
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    public static List<ObjectTypeCount> ReadObjectTypeCounts()
    {
        List<ObjectTypeCount>? result = null;
        var worker = new Thread(() =>
        {
            try { result = QueryAndParse(); }
            catch { result = new List<ObjectTypeCount>(); }
        })
        { IsBackground = true };

        worker.Start();
        bool finished = worker.Join(QueryTimeout);
        return finished ? result ?? new List<ObjectTypeCount>() : new List<ObjectTypeCount>();
    }

    private static List<ObjectTypeCount> QueryAndParse()
    {
        int size = 1 << 16;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQueryObject(IntPtr.Zero, ObjectTypesInformation, buffer, size, out int returnLength);
                if (status == StatusInfoLengthMismatch || status == StatusBufferTooSmall)
                {
                    size = returnLength > size ? returnLength + 0x1000 : size * 2;
                    continue;
                }
                if (status != 0) return new List<ObjectTypeCount>();

                return ParseObjectTypes(buffer, size);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return new List<ObjectTypeCount>();
    }

    private static List<ObjectTypeCount> ParseObjectTypes(IntPtr buffer, int bufferSize)
    {
        var rows = new List<ObjectTypeCount>();
        try
        {
            uint numberOfTypes = (uint)Marshal.ReadInt32(buffer);
            if (numberOfTypes == 0 || numberOfTypes > 512) return rows; // implausible - bail to hidden

            int structSize = Marshal.SizeOf<OBJECT_TYPE_INFORMATION>();
            long offset = IntPtr.Size; // header is one ULONG, padded to pointer size before the array

            for (int i = 0; i < numberOfTypes; i++)
            {
                if (offset < 0 || offset + structSize > bufferSize) break; // out of bounds - stop, keep what we have

                var info = Marshal.PtrToStructure<OBJECT_TYPE_INFORMATION>(IntPtr.Add(buffer, (int)offset));

                string typeName = "(unknown)";
                if (info.TypeName.Length > 0 && info.TypeName.Length < 256 && info.TypeName.Buffer != IntPtr.Zero)
                {
                    string? s = Marshal.PtrToStringUni(info.TypeName.Buffer, info.TypeName.Length / 2);
                    if (!string.IsNullOrWhiteSpace(s)) typeName = s;
                }

                rows.Add(new ObjectTypeCount
                {
                    TypeName = typeName,
                    TotalNumberOfObjects = info.TotalNumberOfObjects,
                    TotalNumberOfHandles = info.TotalNumberOfHandles,
                    HighWaterNumberOfObjects = info.HighWaterNumberOfObjects,
                    HighWaterNumberOfHandles = info.HighWaterNumberOfHandles,
                });

                // The name buffer immediately follows this struct; the next entry is realigned to
                // a pointer-size boundary - the same layout convention Process Hacker and other
                // NtQueryObject(ObjectTypesInformation) callers use for this specific info class.
                long next = offset + structSize + info.TypeName.MaximumLength;
                offset = (next + (IntPtr.Size - 1)) & ~(IntPtr.Size - 1);
            }
        }
        catch
        {
            // Any failure mid-walk (bad offset, unexpected field) - return whatever parsed cleanly
            // so far rather than propagating a struct-layout exception up to the UI.
        }
        return rows;
    }

    private const int ObjectTypesInformation = 3;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_TYPE_INFORMATION
    {
        public UNICODE_STRING TypeName;
        public uint TotalNumberOfObjects;
        public uint TotalNumberOfHandles;
        public uint TotalPagedPoolUsage;
        public uint TotalNonPagedPoolUsage;
        public uint TotalNamePoolUsage;
        public uint TotalHandleTableUsage;
        public uint HighWaterNumberOfObjects;
        public uint HighWaterNumberOfHandles;
        public uint HighWaterPagedPoolUsage;
        public uint HighWaterNonPagedPoolUsage;
        public uint HighWaterNamePoolUsage;
        public uint HighWaterHandleTableUsage;
        public uint InvalidAttributes;
        public uint GenericRead;
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
        public uint ValidAccessMask;
        public byte SecurityRequired;
        public byte MaintainHandleCount;
        public byte TypeIndex;
        public byte ReservedByte;
        public uint PoolType;
        public uint DefaultPagedPoolCharge;
        public uint DefaultNonPagedPoolCharge;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryObject(IntPtr handle, int objectInformationClass, IntPtr objectInformation, int objectInformationLength, out int returnLength);
}
