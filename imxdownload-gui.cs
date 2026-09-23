//
//  imxdownload-gui.cs  —  Windows 图形界面版 i.MX6ULL 裸机镜像烧写工具
//
//  移植自正点原子《阿尔法 Linux 开发板》配套的 Ubuntu 版 imxdownload
//  （imxdownload.c / imxdownload.h，Edit by zuozhongkai，2019/6/10，V1.1）
//
//  编译（用 Windows 自带的 csc，不需要 Visual Studio）：
//    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /optimize+ /codepage:65001 /out:imxdownload-gui.exe imxdownload-gui.cs
//
//  搞定后双击 imxdownload-gui.exe 即可 —— 程序启动时会自动请求管理员权限
//  （UAC 提权由 Main() 里的 TryRestartElevated 完成，不需要额外加 manifest）。
//
//  命令行参数：
//    （无）            目标磁盘列表锁定为「只显示可移动磁盘」，界面上没有开关可取消。
//    --allow-fixed    放开过滤，固定磁盘也会列出来（会额外弹二次确认）。
//                     只在「读卡器把自己上报成固定盘、导致选不到卡」时才需要，
//                     因为那种设备如果完全没口子就永远烧不了。
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

// =====================================================================
//  核心逻辑：与原 imxdownload.c 等价
// =====================================================================
internal static class ImxCore
{
    // #define BIN_OFFSET (3072)
    internal const int BinOffset = 3072;

    // IVT/DCD 表实际占前 1KB（imxdownload.h 里是 int[256]），其余 2048 字节为 0
    internal const int TableBytes = 1024;

    // dd bs=512 seek=2  ==  偏移 1024 字节写入
    internal const long WriteOffset = 1024L;
    internal const int SectorSize = 512;

    private const uint GENERIC_READ = 0x80000000u;
    private const uint GENERIC_WRITE = 0x40000000u;
    private const uint FILE_SHARE_READ = 0x00000001u;
    private const uint FILE_SHARE_WRITE = 0x00000002u;
    private const uint OPEN_EXISTING = 3u;

    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0u;
    private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000u;
    private const uint FSCTL_LOCK_VOLUME = 0x00090018u;
    private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020u;

    private const int MEDIA_TYPE_REMOVABLE = 11;
    private const int MEDIA_TYPE_FIXED = 12;

    internal const uint IvtMagic = 0x402000D1u;

    // ---------------- IVT/DCD 表（照抄 imxdownload.h，已逐值核对 256/256 一致） ----------------

    internal static readonly uint[] Table512 = new uint[]
    {
        0x402000D1u,0x87800000u,0x00000000u,0x877FF42Cu,0x877FF420u,0x877FF400u,0x00000000u,0x00000000u,
        0x877FF000u,0x00200000u,0x00000000u,0x40E801D2u,0x04E401CCu,0x68400C02u,0xFFFFFFFFu,0x6C400C02u,
        0xFFFFFFFFu,0x70400C02u,0xFFFFFFFFu,0x74400C02u,0xFFFFFFFFu,0x78400C02u,0xFFFFFFFFu,0x7C400C02u,
        0xFFFFFFFFu,0x80400C02u,0xFFFFFFFFu,0xB4040E02u,0x00000C00u,0xAC040E02u,0x00000000u,0x7C020E02u,
        0x30000000u,0x50020E02u,0x30000000u,0x4C020E02u,0x30000000u,0x90040E02u,0x30000000u,0x88020E02u,
        0x30000C00u,0x70020E02u,0x00000000u,0x60020E02u,0x30000000u,0x64020E02u,0x30000000u,0xA0040E02u,
        0x30000000u,0x94040E02u,0x00000200u,0x80020E02u,0x30000000u,0x84020E02u,0x30000000u,0xB0040E02u,
        0x00000200u,0x98040E02u,0x30000000u,0xA4040E02u,0x30000000u,0x44020E02u,0x30000000u,0x48020E02u,
        0x30000000u,0x1C001B02u,0x00800000u,0x00081B02u,0x030039A1u,0x0C081B02u,0x0B000300u,0x3C081B02u,
        0x44014801u,0x48081B02u,0x302C4040u,0x50081B02u,0x343E4040u,0x1C081B02u,0x33333333u,0x20081B02u,
        0x33333333u,0x2C081B02u,0x333333F3u,0x30081B02u,0x333333F3u,0xC0081B02u,0x09409400u,0xB8081B02u,
        0x00080000u,0x04001B02u,0x2D000200u,0x08001B02u,0x3030331Bu,0x0C001B02u,0xF3526B67u,0x10001B02u,
        0x630B6DB6u,0x14001B02u,0xDB00FF01u,0x18001B02u,0x40172000u,0x1C001B02u,0x00800000u,0x2C001B02u,
        0xD2260000u,0x30001B02u,0x23106B00u,0x40001B02u,0x4F000000u,0x00001B02u,0x00001884u,0x90081B02u,
        0x00004000u,0x1C001B02u,0x32800002u,0x1C001B02u,0x33800000u,0x1C001B02u,0x31800400u,0x1C001B02u,
        0x30802015u,0x1C001B02u,0x40800004u,0x20001B02u,0x00080000u,0x18081B02u,0x27020000u,0x04001B02u,
        0x2D550200u,0x04041B02u,0x06100100u,0x1C001B02u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u
    };

    internal static readonly uint[] Table256 = new uint[]
    {
        0x402000D1u,0x87800000u,0x00000000u,0x877FF42Cu,0x877FF420u,0x877FF400u,0x00000000u,0x00000000u,
        0x877FF000u,0x00076000u,0x00000000u,0x40E801D2u,0x04E401CCu,0x68400C02u,0xFFFFFFFFu,0x6C400C02u,
        0xFFFFFFFFu,0x70400C02u,0xFFFFFFFFu,0x74400C02u,0xFFFFFFFFu,0x78400C02u,0xFFFFFFFFu,0x7C400C02u,
        0xFFFFFFFFu,0x80400C02u,0xFFFFFFFFu,0xB4040E02u,0x00000C00u,0xAC040E02u,0x00000000u,0x7C020E02u,
        0x30000000u,0x50020E02u,0x30000000u,0x4C020E02u,0x30000000u,0x90040E02u,0x30000000u,0x88020E02u,
        0x30000C00u,0x70020E02u,0x00000000u,0x60020E02u,0x30000000u,0x64020E02u,0x30000000u,0xA0040E02u,
        0x30000000u,0x94040E02u,0x00000200u,0x80020E02u,0x30000000u,0x84020E02u,0x30000000u,0xB0040E02u,
        0x00000200u,0x98040E02u,0x30000000u,0xA4040E02u,0x30000000u,0x44020E02u,0x30000000u,0x48020E02u,
        0x30000000u,0x1C001B02u,0x00800000u,0x00081B02u,0x030039A1u,0x0C081B02u,0x04000000u,0x3C081B02u,
        0x3C013C01u,0x48081B02u,0x38324040u,0x50081B02u,0x28304040u,0x1C081B02u,0x33333333u,0x20081B02u,
        0x33333333u,0x2C081B02u,0x333333F3u,0x30081B02u,0x333333F3u,0xC0081B02u,0x09409400u,0xB8081B02u,
        0x00080000u,0x04001B02u,0x2D000200u,0x08001B02u,0x3030331Bu,0x0C001B02u,0xF352433Fu,0x10001B02u,
        0x630B6DB6u,0x14001B02u,0xDB00FF01u,0x18001B02u,0x40172000u,0x1C001B02u,0x00800000u,0x2C001B02u,
        0xD2260000u,0x30001B02u,0x23104300u,0x40001B02u,0x47000000u,0x00001B02u,0x00001883u,0x90081B02u,
        0x00004000u,0x1C001B02u,0x32800002u,0x1C001B02u,0x33800000u,0x1C001B02u,0x31800400u,0x1C001B02u,
        0x30802015u,0x1C001B02u,0x40800004u,0x20001B02u,0x00080000u,0x18081B02u,0x27020000u,0x04001B02u,
        0x2D550200u,0x04041B02u,0x06100100u,0x1C001B02u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,
        0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u,0x00000000u
    };

    // ---------------- P/Invoke ----------------

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlBuf(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlNone(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    // ---------------- 数据结构 ----------------

    internal sealed class DiskInfo
    {
        public int Index;
        public long Size;
        public int MediaType;
        public string Letters = "";

        public bool IsRemovable { get { return MediaType == MEDIA_TYPE_REMOVABLE; } }

        public string TypeText
        {
            get
            {
                if (MediaType == MEDIA_TYPE_REMOVABLE) return "可移动";
                if (MediaType == MEDIA_TYPE_FIXED) return "固定";
                return "未知";
            }
        }

        public override string ToString()
        {
            return "磁盘 " + Index.ToString(CultureInfo.InvariantCulture)
                 + "    " + FormatSize(Size)
                 + "    " + TypeText
                 + (Letters.Length > 0 ? "    " + Letters : "");
        }
    }

    internal sealed class DetectResult
    {
        public bool ValidIvt;
        public uint FirstWord;
        public uint ImageSize;
        public int Match512;
        public int Match256;
        public int DiffTotal;
        public int Hit512;
        public int Hit256;
    }

    // ---------------- 镜像组装（与 dd bs=512 seek=2 等价） ----------------

    // 生成与官方 imxdownload 完全一致的镜像：长度严格 = 3072 + bin，尾部不补齐。
    internal static byte[] BuildImage(byte[] bin, bool use256M)
    {
        uint[] table = use256M ? Table256 : Table512;

        byte[] image = new byte[BinOffset + bin.Length];

        // 头部前 1KB：IVT/DCD 表，小端写入
        for (int i = 0; i < TableBytes / 4; i++)
        {
            byte[] le = BitConverter.GetBytes(table[i]);
            Buffer.BlockCopy(le, 0, image, i * 4, 4);
        }

        // 代码接在 3072 之后
        Buffer.BlockCopy(bin, 0, image, BinOffset, bin.Length);
        return image;
    }

    // 写盘专用：\\.\PhysicalDriveN 要求长度按扇区对齐，只在真正写盘时把尾部补 0。
    internal static byte[] PadToSector(byte[] image, out int padBytes)
    {
        int padded = ((image.Length + SectorSize - 1) / SectorSize) * SectorSize;
        padBytes = padded - image.Length;
        if (padBytes == 0) return image;
        byte[] w = new byte[padded];
        Buffer.BlockCopy(image, 0, w, 0, image.Length);
        return w;
    }

    // ---------------- 检测已有 .imx 用的是哪张表 ----------------

    internal static DetectResult Detect(byte[] data)
    {
        DetectResult r = new DetectResult();
        if (data == null || data.Length < TableBytes) return r;

        uint[] vals = new uint[256];
        for (int i = 0; i < 256; i++) vals[i] = BitConverter.ToUInt32(data, i * 4);

        r.ValidIvt = (vals[0] == IvtMagic);
        r.FirstWord = vals[0];
        r.ImageSize = vals[9];

        for (int i = 0; i < 256; i++)
        {
            if (vals[i] == Table512[i]) r.Match512++;
            if (vals[i] == Table256[i]) r.Match256++;
        }

        for (int i = 0; i < 256; i++)
        {
            if (Table512[i] == Table256[i]) continue;
            r.DiffTotal++;
            if (vals[i] == Table512[i]) r.Hit512++;
            else if (vals[i] == Table256[i]) r.Hit256++;
        }
        return r;
    }

    // ---------------- 磁盘枚举 ----------------

    internal static List<DiskInfo> EnumerateDisks()
    {
        List<DiskInfo> list = new List<DiskInfo>();
        for (int i = 0; i < 16; i++)
        {
            string path = DevPath(i);
            SafeFileHandle h = CreateFile(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }

            DiskInfo info = null;
            try
            {
                byte[] buf = new byte[64];
                int ret;
                if (DeviceIoControlBuf(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                        IntPtr.Zero, 0, buf, buf.Length, out ret, IntPtr.Zero))
                {
                    // DISK_GEOMETRY 占 24 字节，紧接其后是 DiskSize(8 字节)
                    info = new DiskInfo();
                    info.Index = i;
                    info.MediaType = BitConverter.ToInt32(buf, 8);
                    info.Size = BitConverter.ToInt64(buf, 24);
                }
            }
            finally { h.Dispose(); }

            if (info != null)
            {
                info.Letters = GetVolumeLetters(i);
                list.Add(info);
            }
        }
        return list;
    }

    private static string GetVolumeLetters(int diskIndex)
    {
        List<string> hits = new List<string>();
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string vp = @"\\.\" + c + ":";
            SafeFileHandle h = CreateFile(vp, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            try
            {
                if (VolumeOnDisk(h, diskIndex)) hits.Add(c + ":");
            }
            finally { h.Dispose(); }
        }
        return string.Join(",", hits.ToArray());
    }

    private static bool VolumeOnDisk(SafeFileHandle volume, int diskIndex)
    {
        byte[] buf = new byte[1024];
        int ret;
        if (!DeviceIoControlBuf(volume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                IntPtr.Zero, 0, buf, buf.Length, out ret, IntPtr.Zero))
            return false;

        int count = BitConverter.ToInt32(buf, 0);
        for (int k = 0; k < count; k++)
        {
            // VOLUME_DISK_EXTENTS: DWORD 之后 4 字节对齐填充；DISK_EXTENT 每项 24 字节
            int off = 8 + k * 24;
            if (off + 24 > buf.Length) break;
            if (BitConverter.ToInt32(buf, off) == diskIndex) return true;
        }
        return false;
    }

    // 锁定并卸载目标盘上的卷，避免 Windows 缓存回写把刚写入的数据盖掉
    internal static void DismountVolumes(int diskIndex, Action<string> log)
    {
        bool any = false;
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string vp = @"\\.\" + c + ":";
            SafeFileHandle h = CreateFile(vp, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { h.Dispose(); continue; }
            try
            {
                if (!VolumeOnDisk(h, diskIndex)) continue;
                any = true;
                int ret;
                bool locked = DeviceIoControlNone(h, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                bool dismounted = DeviceIoControlNone(h, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);
                log("  卷 " + c + ":   锁定=" + (locked ? "成功" : "失败") + "   卸载=" + (dismounted ? "成功" : "失败"));
                if (!locked)
                    log("    （锁不住一般是有程序占用该盘：资源管理器窗口、杀毒软件、播放器预览等，关掉再试）");
            }
            finally { h.Dispose(); }
        }
        if (!any) log("  该磁盘上没有已挂载的卷，跳过。");
    }

    // ---------------- 写入与校验 ----------------

    internal static bool WriteToDisk(int diskIndex, byte[] image, out string error)
    {
        error = null;
        string dev = DevPath(diskIndex);
        SafeFileHandle h = CreateFile(dev, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            error = "打不开 " + dev + "（Win32 错误码 " + err + "）。"
                  + (err == 5 ? "这是权限不足，请以「管理员身份运行」本程序。" : "请确认磁盘编号正确、读卡器已插好。");
            return false;
        }

        try
        {
            using (FileStream fs = new FileStream(h, FileAccess.Write))
            {
                int pad;
                byte[] writeBuf = PadToSector(image, out pad);
                fs.Seek(WriteOffset, SeekOrigin.Begin);
                fs.Write(writeBuf, 0, writeBuf.Length);
                fs.Flush(true);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool VerifyOnDisk(int diskIndex, byte[] image, out int diffCount, out string error)
    {
        diffCount = -1;
        error = null;
        string dev = DevPath(diskIndex);
        SafeFileHandle h = CreateFile(dev, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); error = "回读时打不开 " + dev; return false; }

        int padDummy;
        byte[] expect = PadToSector(image, out padDummy);
        byte[] back = new byte[expect.Length];
        try
        {
            using (FileStream fs = new FileStream(h, FileAccess.Read))
            {
                fs.Seek(WriteOffset, SeekOrigin.Begin);
                int total = 0;
                while (total < back.Length)
                {
                    int n = fs.Read(back, total, back.Length - total);
                    if (n <= 0) { error = "回读时提前到达磁盘末尾"; return false; }
                    total += n;
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        int diff = 0;
        for (int i = 0; i < expect.Length; i++) if (expect[i] != back[i]) diff++;
        diffCount = diff;
        return true;
    }

    internal static string DevPath(int index)
    {
        return @"\\.\PhysicalDrive" + index.ToString(CultureInfo.InvariantCulture);
    }

    internal static string FormatSize(long bytes)
    {
        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
        if (gb >= 1.0) return gb.ToString("F1", CultureInfo.InvariantCulture) + " GB";
        double mb = bytes / 1024.0 / 1024.0;
        return mb.ToString("F1", CultureInfo.InvariantCulture) + " MB";
    }

    // 当前进程是否以管理员身份运行（决定能不能打开 \\.\PhysicalDriveN 写）
    internal static bool IsAdministrator()
    {
        try
        {
            WindowsIdentity id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

// =====================================================================
//  图形界面
// =====================================================================
internal sealed class MainForm : Form
{
    private TextBox txtBin;
    private Button btnBrowse;
    private RadioButton rb512;
    private RadioButton rb256;
    // 命令行开关：--allow-fixed 才允许把固定磁盘列出来。
    // 默认锁死只显示「可移动」，界面上没有开关可以取消，避免手滑选错盘。
    // 留这个命令行口子是为了兜底：部分 USB 读卡器 / USB-SATA 盒会把自己上报成
    // 固定盘，那种设备如果完全没口子，就永远选不到卡、工具直接不可用了。
    internal static bool AllowFixed;

    private ComboBox cmbDisk;
    private Button btnRefresh;
    private Label lblDiskInfo;
    private Button btnMakeImage;
    private Button btnDetect;
    private Button btnBurn;
    private ProgressBar pb;
    private TextBox txtLog;
    private Label lblStatus;

    private bool busy;

    internal MainForm()
    {
        Text = "i.MX6ULL 镜像烧写工具  —  Windows 版  (移植自正点原子 imxdownload)";
        ClientSize = new Size(720, 648);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(740, 548);
        Font = new Font("微软雅黑", 9F, FontStyle.Regular, GraphicsUnit.Point);

        // ---- 第 1 组：固件文件 ----
        GroupBox gb1 = new GroupBox();
        gb1.Text = "1. 选择固件（.bin）";
        gb1.Location = new Point(12, 12);
        gb1.Size = new Size(696, 64);
        gb1.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(gb1);

        Label lb1 = new Label();
        lb1.Text = "固件:";
        lb1.Location = new Point(14, 28);
        lb1.Size = new Size(42, 20);
        gb1.Controls.Add(lb1);

        txtBin = new TextBox();
        txtBin.Location = new Point(60, 25);
        txtBin.Size = new Size(536, 23);
        txtBin.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        gb1.Controls.Add(txtBin);

        btnBrowse = new Button();
        btnBrowse.Text = "浏览...";
        btnBrowse.Location = new Point(604, 24);
        btnBrowse.Size = new Size(80, 25);
        btnBrowse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBrowse.Click += new EventHandler(btnBrowse_Click);
        gb1.Controls.Add(btnBrowse);

        // ---- 第 2 组：DDR 容量 ----
        GroupBox gb2 = new GroupBox();
        gb2.Text = "2. DDR 容量";
        gb2.Location = new Point(12, 84);
        gb2.Size = new Size(340, 96);
        Controls.Add(gb2);

        rb512 = new RadioButton();
        rb512.Text = "512MB DDR3";
        rb512.Location = new Point(16, 26);
        rb512.Size = new Size(120, 22);
        rb512.Checked = true;
        gb2.Controls.Add(rb512);

        rb256 = new RadioButton();
        rb256.Text = "256MB DDR3";
        rb256.Location = new Point(150, 26);
        rb256.Size = new Size(120, 22);
        gb2.Controls.Add(rb256);

        Label lb2 = new Label();
        lb2.Text = "选错 DDR 参数 = 板子起不来，务必确认";
        lb2.Location = new Point(16, 50);
        lb2.Size = new Size(310, 18);
        lb2.ForeColor = Color.FromArgb(176, 0, 32);
        gb2.Controls.Add(lb2);

        Label lb2b = new Label();
        lb2b.Text = "注：《01_leds》例程的 load.imx 用的其实是 256MB 表";
        lb2b.Location = new Point(16, 70);
        lb2b.Size = new Size(316, 18);
        lb2b.ForeColor = Color.FromArgb(80, 80, 80);
        gb2.Controls.Add(lb2b);

        // ---- 第 3 组：目标磁盘 ----
        GroupBox gb3 = new GroupBox();
        gb3.Text = "3. 目标磁盘";
        gb3.Location = new Point(364, 84);
        gb3.Size = new Size(344, 96);
        Controls.Add(gb3);

        cmbDisk = new ComboBox();
        cmbDisk.Location = new Point(12, 20);
        cmbDisk.Size = new Size(240, 23);
        cmbDisk.DropDownStyle = ComboBoxStyle.DropDownList;
        cmbDisk.DropDownWidth = 320;
        cmbDisk.SelectedIndexChanged += new EventHandler(cmbDisk_SelectedIndexChanged);
        gb3.Controls.Add(cmbDisk);

        btnRefresh = new Button();
        btnRefresh.Text = "刷新";
        btnRefresh.Location = new Point(258, 19);
        btnRefresh.Size = new Size(62, 25);
        btnRefresh.Click += new EventHandler(btnRefresh_Click);
        gb3.Controls.Add(btnRefresh);

        lblDiskInfo = new Label();
        lblDiskInfo.Text = "（点「刷新」枚举磁盘）";
        lblDiskInfo.Location = new Point(12, 46);
        lblDiskInfo.Size = new Size(316, 18);
        lblDiskInfo.ForeColor = Color.FromArgb(80, 80, 80);
        gb3.Controls.Add(lblDiskInfo);

        // 这里没有「取消过滤」的开关，是故意的：过滤锁死，不给手滑的机会。
        // 万一你的读卡器把自己上报成固定盘而选不到卡，见下面的提示。
        Label lbLock = new Label();
        lbLock.Text = "已锁定：只列可移动盘，固定盘不进这个列表";
        lbLock.Location = new Point(12, 68);
        lbLock.Size = new Size(316, 18);
        lbLock.ForeColor = Color.FromArgb(0, 96, 48);
        gb3.Controls.Add(lbLock);

        // ---- 操作按钮 ----
        btnMakeImage = new Button();
        btnMakeImage.Text = "只生成镜像文件...";
        btnMakeImage.Location = new Point(12, 192);
        btnMakeImage.Size = new Size(220, 32);
        btnMakeImage.Click += new EventHandler(btnMakeImage_Click);
        Controls.Add(btnMakeImage);

        btnDetect = new Button();
        btnDetect.Text = "检测 .imx 的 DDR 表...";
        btnDetect.Location = new Point(248, 192);
        btnDetect.Size = new Size(220, 32);
        btnDetect.Click += new EventHandler(btnDetect_Click);
        Controls.Add(btnDetect);

        btnBurn = new Button();
        btnBurn.Text = "开始烧写";
        btnBurn.Location = new Point(488, 192);
        btnBurn.Size = new Size(220, 32);
        btnBurn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnBurn.Font = new Font(this.Font, FontStyle.Bold);
        btnBurn.Click += new EventHandler(btnBurn_Click);
        Controls.Add(btnBurn);

        // ---- 进度条 ----
        pb = new ProgressBar();
        pb.Location = new Point(12, 232);
        pb.Size = new Size(696, 16);
        pb.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        pb.Style = ProgressBarStyle.Blocks;
        Controls.Add(pb);

        // ---- 日志框 ----
        txtLog = new TextBox();
        txtLog.Location = new Point(12, 256);
        txtLog.Size = new Size(696, 348);
        txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        txtLog.Multiline = true;
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Both;
        txtLog.WordWrap = false;
        txtLog.BackColor = Color.FromArgb(252, 252, 250);
        txtLog.Font = new Font("Consolas", 9F, FontStyle.Regular, GraphicsUnit.Point);
        Controls.Add(txtLog);

        // ---- 状态栏 ----
        lblStatus = new Label();
        lblStatus.Text = "就绪。先选固件与 DDR 容量，再选目标磁盘。";
        lblStatus.Location = new Point(12, 612);
        lblStatus.Size = new Size(696, 20);
        lblStatus.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(lblStatus);

        // 注意：这里刻意【不】用 WinForms 的 AllowDrop / DragEnter / DragDrop。
        // 那是一套 OLE 拖放，走的是跨进程 COM 调用，会被 UIPI 拦掉 —— 一旦本程序
        // 以管理员身份运行，从资源管理器（中完整性）拖进来的文件就再也收不到了。
        // 改用传统的 WM_DROPFILES：在 MainForm_Load 里注册 DragAcceptFiles 并用
        // ChangeWindowMessageFilterEx 放行该消息，这样提权之后照样能拖。
        // 详见 WndProc 与 SetupLegacyDropTarget()。

        Load += new EventHandler(MainForm_Load);
    }

    // ---------------- 生命周期 ----------------

    private void MainForm_Load(object sender, EventArgs e)
    {
        SetupLegacyDropTarget();

        Log("i.MX6ULL 镜像烧写工具  —  Windows / 图形界面版");
        Log("移植自正点原子《阿尔法 Linux 开发板》的 imxdownload.c (zuozhongkai, 2019/6/10, V1.1)");
        Log("");
        Log("镜像布局（与原工具 dd bs=512 seek=2 完全一致）：");
        Log("    偏移 0    ~ 1023   IVT + DCD 表（1KB）");
        Log("    偏移 1024 ~ 3071   补 0");
        Log("    偏移 3072 ~        你的代码");
        Log("    整块镜像写入目标磁盘的偏移 1024 处");
        Log("");
        Log("提示：把 .bin 文件直接拖进窗口也能选中。");
        Log(ImxCore.IsAdministrator()
            ? "权限：已以【管理员】身份运行，可以写物理磁盘。"
            : "权限：当前是【普通用户】，烧写会失败 —— 请关掉后右键 exe／以管理员身份运行。");
        if (AllowFixed)
        {
            Log("磁盘过滤：已放开（本次带 --allow-fixed 启动）。");
            Log("          固定磁盘也会出现在列表里 —— 请格外小心，写错会毁硬盘数据！");
        }
        else
        {
            Log("磁盘过滤：已锁定，只列出「可移动」磁盘，固定盘不会出现在下拉框里。");
        }
        Log("");
        RefreshDisks();
    }

    // ---------------- 日志 ----------------

    private void Log(string text)
    {
        if (txtLog.IsDisposed) return;
        if (txtLog.InvokeRequired)
        {
            txtLog.BeginInvoke(new Action<string>(Log), new object[] { text });
            return;
        }
        txtLog.AppendText(text + "\r\n");
        txtLog.SelectionStart = txtLog.TextLength;
        txtLog.ScrollToCaret();
    }

    private void Status(string text)
    {
        if (lblStatus.IsDisposed) return;
        if (lblStatus.InvokeRequired)
        {
            lblStatus.BeginInvoke(new Action<string>(Status), new object[] { text });
            return;
        }
        lblStatus.Text = text;
    }

    private void SetBusy(bool value)
    {
        busy = value;
        btnBurn.Enabled = !value;
        btnMakeImage.Enabled = !value;
        btnDetect.Enabled = !value;
        btnBrowse.Enabled = !value;
        btnRefresh.Enabled = !value;
        cmbDisk.Enabled = !value && cmbDisk.Items.Count > 0;
        if (value)
        {
            pb.Style = ProgressBarStyle.Marquee;
            pb.MarqueeAnimationSpeed = 30;
            Cursor = Cursors.WaitCursor;
        }
        else
        {
            pb.MarqueeAnimationSpeed = 0;
            pb.Style = ProgressBarStyle.Blocks;
            pb.Value = 0;
            Cursor = Cursors.Default;
        }
    }

    private void RunAsync(ThreadStart work)
    {
        if (busy) return;
        SetBusy(true);
        Thread t = new Thread(delegate()
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log("发生错误: " + ex.Message);
                Status("出错，详见日志。");
            }
            finally
            {
                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(new Action(delegate()
                    {
                        if (!IsDisposed) SetBusy(false);
                    }));
                }
            }
        });
        t.IsBackground = true;
        t.Start();
    }

    // ---------------- 拖放（传统 WM_DROPFILES，提权后依然可用） ----------------

    private const int WM_DROPFILES = 0x0233;
    private const int WM_COPYDATA = 0x004A;
    private const int WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr changeInfo);

    // 老 API：对整个进程放行（不挑接收窗口）。作为 Ex 版本的保险，两个都调。
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilter(uint msg, uint flag);

    // 把本窗口（含所有子控件）注册成「接受传统拖放」的目标，并放行会被 UIPI 拦掉的
    // 三个消息。不做这一步的话：以管理员身份运行时，从资源管理器拖过来的文件收不到
    // —— 因为资源管理器是中完整性进程，而本程序提权后是高完整性，UIPI 默认阻断
    // 低完整性进程往高完整性窗口发的窗口消息。
    //
    // 之所以要遍历子控件：WM_DROPFILES 是由外壳程序投递给「鼠标底下的那个窗口」的，
    // 如果那个窗口没有 WS_EX_ACCEPTFILES，外壳才会上溯父窗口。所以干脆给每个窗口
    // 都登记一遍，配合下面的消息过滤器，无论拖到哪个控件上都能收到。
    private void SetupLegacyDropTarget()
    {
        // 进程级放行（粗粒度，双保险）
        ChangeWindowMessageFilter(WM_DROPFILES, MSGFLT_ALLOW);
        ChangeWindowMessageFilter(WM_COPYGLOBALDATA, MSGFLT_ALLOW);
        ChangeWindowMessageFilter(WM_COPYDATA, MSGFLT_ALLOW);

        // 窗口级放行 + 登记为拖放目标（每个窗口都来一遍）
        ApplyDropTarget(this);

        // 消息队列层的兜底拦截
        Application.AddMessageFilter(new DropMessageFilter(this));
    }

    private static void ApplyDropTarget(Control c)
    {
        try
        {
            IntPtr h = c.Handle;
            if (h != IntPtr.Zero)
            {
                DragAcceptFiles(h, true);
                ChangeWindowMessageFilterEx(h, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(h, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(h, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
            }
        }
        catch
        {
            // 某个控件登记失败不影响其它控件，忽略即可
        }
        foreach (Control child in c.Controls) ApplyDropTarget(child);
    }

    // WM_DROPFILES 是「投递」消息，会先过线程的消息队列，所以在这里能拦到发给
    // 任意子窗口的拖放，比只靠窗口链上溯可靠。返回 true 表示已处理，不再往下派发。
    private sealed class DropMessageFilter : IMessageFilter
    {
        private readonly MainForm owner;

        internal DropMessageFilter(MainForm o) { owner = o; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_DROPFILES) return false;
            try
            {
                owner.HandleDroppedFiles(m.WParam);
            }
            catch (Exception ex)
            {
                owner.Log("拖放处理失败: " + ex.Message);
            }
            return true;
        }
    }

    // 兜底：万一消息过滤器没拦到（理论上不会），这里再处理一次。
    // 过滤器先返回 true 就不会走到这，所以不存在重复处理。
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DROPFILES)
        {
            try
            {
                HandleDroppedFiles(m.WParam);
            }
            catch (Exception ex)
            {
                Log("拖放处理失败: " + ex.Message);
            }
            return;   // WM_DROPFILES 必须自己 DragFinish，不能交给 base
        }
        base.WndProc(ref m);
    }

    private void HandleDroppedFiles(IntPtr hDrop)
    {
        try
        {
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            if (count == 0) return;

            string firstFile = null;
            string firstImx = null;
            List<string> names = new List<string>();

            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFile(hDrop, i, null, 0);
                if (len == 0) continue;
                StringBuilder sb = new StringBuilder((int)len + 1);
                DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                string path = sb.ToString();
                names.Add(Path.GetFileName(path));

                if (Directory.Exists(path)) continue;

                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (firstFile == null && ext == ".bin") firstFile = path;
                if (firstImx == null && ext == ".imx") firstImx = path;
            }

            Log("拖入 " + count + " 项: " + string.Join(", ", names.ToArray()));

            if (firstFile != null)
            {
                txtBin.Text = firstFile;
                Log("已选择固件: " + firstFile);
                Status("固件已就绪：" + Path.GetFileName(firstFile));
                return;
            }

            if (firstImx != null)
            {
                Log("这个 .imx 不是 .bin 固件。想烧写请拖入 <你的代码>.bin；");
                Log("想查看 DDR 配置，请用「检测 .imx 的 DDR 表...」按钮选它。");
                Status("拖入的是 .imx，不是固件。");
                return;
            }

            Log("拖入的内容里没有 .bin 固件，已忽略。");
            Log("（本工具烧写的是 arm-linux-gnueabihf-gcc 编译出来的 .bin，不是 .elf/.imx）");
            Status("拖入的不是 .bin 固件。");
        }
        finally
        {
            DragFinish(hDrop);
        }
    }

    // ---------------- 事件 ----------------

    private void btnBrowse_Click(object sender, EventArgs e)
    {
        OpenFileDialog dlg = new OpenFileDialog();
        dlg.Title = "选择要烧写的固件";
        dlg.Filter = "二进制固件 (*.bin)|*.bin|所有文件 (*.*)|*.*";
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            txtBin.Text = dlg.FileName;
            Log("已选择固件: " + dlg.FileName);
        }
    }

    private void btnRefresh_Click(object sender, EventArgs e)
    {
        RefreshDisks();
    }

    private void cmbDisk_SelectedIndexChanged(object sender, EventArgs e)
    {
        ImxCore.DiskInfo d = cmbDisk.SelectedItem as ImxCore.DiskInfo;
        if (d == null)
        {
            lblDiskInfo.Text = "（未选择）";
            return;
        }
        lblDiskInfo.Text = ImxCore.FormatSize(d.Size) + "  ·  " + d.TypeText
                         + (d.Letters.Length > 0 ? "  ·  " + d.Letters : "  ·  无盘符");
    }

    private void RefreshDisks()
    {
        Cursor = Cursors.WaitCursor;
        int keep = -1;
        ImxCore.DiskInfo cur = cmbDisk.SelectedItem as ImxCore.DiskInfo;
        if (cur != null) keep = cur.Index;

        cmbDisk.Items.Clear();
        cmbDisk.Enabled = false;
        try
        {
            List<ImxCore.DiskInfo> all;
            try
            {
                all = ImxCore.EnumerateDisks();
            }
            catch (Exception ex)
            {
                Log("枚举磁盘失败: " + ex.Message);
                Status("枚举磁盘失败。");
                return;
            }

            Log("枚举到 " + all.Count + " 块物理磁盘：");
            foreach (ImxCore.DiskInfo d in all)
                Log("    " + d.ToString() + (d.IsRemovable ? "    <- 可移动" : "    <- 固定盘"));

            // ---- 过滤：锁死，只留「可移动」----
            // 界面上没有开关可以取消这个过滤，是故意的：不给手滑的机会。
            // 唯一的例外是启动时带了 --allow-fixed（命令行手动加），那种情况下面会额外警告。
            bool showFixed = AllowFixed;
            List<ImxCore.DiskInfo> shown = new List<ImxCore.DiskInfo>();
            foreach (ImxCore.DiskInfo d in all)
                if (showFixed || d.IsRemovable) shown.Add(d);

            int removableCount = 0;
            foreach (ImxCore.DiskInfo d in all) if (d.IsRemovable) removableCount++;

            foreach (ImxCore.DiskInfo d in shown) cmbDisk.Items.Add(d);

            if (cmbDisk.Items.Count > 0)
            {
                int sel = 0;
                for (int i = 0; i < cmbDisk.Items.Count; i++)
                {
                    ImxCore.DiskInfo d = (ImxCore.DiskInfo)cmbDisk.Items[i];
                    if (d.Index == keep) sel = i;
                }
                cmbDisk.SelectedIndex = sel;

                if (showFixed)
                {
                    Log("");
                    Log("!! 警告：本次是带 --allow-fixed 启动的，固定磁盘也被列出来了！");
                    Log("!! 写错对象会毁掉硬盘里的数据，务必自己按容量认准。");
                    Log("");
                    Status("警告：带了 --allow-fixed，固定盘可被选中。");
                }
                else
                {
                    Log("下拉框已锁定：只有 " + shown.Count + " 块可移动磁盘可选。");
                    Status("只列可移动磁盘，请选择容量小的那块。");
                }
            }
            else
            {
                Status("没有可用的可移动磁盘。");
                Log("");
                if (removableCount == 0)
                {
                    Log("没有找到「可移动」磁盘，但系统里有 " + all.Count + " 块固定磁盘。");
                    Log("两种可能：");
                    Log("  1) 读卡器没插好、卡没插到位 —— 插好后点「刷新」；");
                    Log("  2) 这个读卡器 / 硬盘盒把自己上报成了固定盘（部分 USB 读卡器、");
                    Log("     USB-SATA 硬盘盒会这样）。本程序默认把它挡在外面，这是特性不是故障。");
                    Log("     确实要用它烧写，请带参数启动：");
                    Log("         imxdownload-gui.exe --allow-fixed");
                    Log("     那时固定盘会列出来，但选中后仍会再弹一次二次确认。");
                }
                else
                {
                    Log("没有可用的可移动磁盘。请检查读卡器、卡是否插好，然后点「刷新」。");
                }
            }
        }
        finally
        {
            cmbDisk.Enabled = cmbDisk.Items.Count > 0;
            Cursor = Cursors.Default;
        }
        Log("");
    }

    // ---------------- 只生成镜像文件 ----------------

    private void btnMakeImage_Click(object sender, EventArgs e)
    {
        string bin = txtBin.Text.Trim();
        if (bin.Length == 0 || !File.Exists(bin))
        {
            MessageBox.Show(this, "请先选择一个存在的固件 .bin 文件。", "缺少固件",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SaveFileDialog sfd = new SaveFileDialog();
        sfd.Title = "保存生成的镜像";
        sfd.Filter = "i.MX 镜像 (*.imx)|*.imx|所有文件 (*.*)|*.*";
        sfd.FileName = "load.imx";
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        string outPath = sfd.FileName;
        bool use256 = rb256.Checked;

        RunAsync(delegate()
        {
            Status("正在生成镜像 ...");
            byte[] b = File.ReadAllBytes(bin);
            byte[] img = ImxCore.BuildImage(b, use256);
            File.WriteAllBytes(outPath, img);
            Log("--- 生成镜像 ---");
            Log("固件大小: " + b.Length + " Bytes");
            Log("DDR 配置: " + (use256 ? "256MB" : "512MB"));
            Log("镜像大小: " + img.Length + " Bytes  (头部 3072 + 代码 " + b.Length
                + " + 扇区对齐补零 " + (img.Length - ImxCore.BinOffset - b.Length) + ")");
            Log("已写出: " + Path.GetFullPath(outPath));
            Log("");
            Log("如需烧卡：请直接用「开始烧写」，由本程序写到正确的偏移。");
            Log("注意：本文件必须写到 SD 卡的偏移 1024 字节（第 2 个扇区）处。");
            Log("      balenaEtcher / Win32DiskImager 都是从第 0 扇区开始整盘写，位置不对，");
            Log("      用它们烧出来的卡不能启动。Linux 下请用 dd if=xxx.imx of=/dev/sdX bs=512 seek=2。");
            Status("镜像已生成：" + Path.GetFileName(outPath));
        });
    }

    // ---------------- 检测 .imx ----------------

    private void btnDetect_Click(object sender, EventArgs e)
    {
        OpenFileDialog ofd = new OpenFileDialog();
        ofd.Title = "选择要检测的 .imx 镜像";
        ofd.Filter = "i.MX 镜像 (*.imx)|*.imx|所有文件 (*.*)|*.*";
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        string path = ofd.FileName;
        RunAsync(delegate()
        {
            Status("正在检测 ...");
            byte[] data = File.ReadAllBytes(path);
            ImxCore.DetectResult r = ImxCore.Detect(data);

            Log("--- 检测镜像 ---");
            Log("文件: " + Path.GetFullPath(path));
            Log("大小: " + data.Length + " Bytes");
            if (data.Length < ImxCore.TableBytes)
            {
                Log("文件太小，不是合法的 i.MX 镜像。");
                Status("检测失败：文件过小。");
                Log("");
                return;
            }

            Log("头 4 字节: 0x" + r.FirstWord.ToString("X8", CultureInfo.InvariantCulture)
                + (r.ValidIvt ? "  合法 i.MX IVT 头" : "  不是 0x402000D1，可能不是 i.MX 镜像"));
            Log("表内 image_size 字段 (下标 9): 0x" + r.ImageSize.ToString("X8", CultureInfo.InvariantCulture)
                + "  = " + r.ImageSize + " Bytes");
            Log("");
            Log("全表 256 项匹配数:    512MB 表 " + r.Match512 + " 项 / 256MB 表 " + r.Match256 + " 项");
            Log("两表差异共 " + r.DiffTotal + " 项:   命中 512MB " + r.Hit512 + " 项 / 命中 256MB " + r.Hit256 + " 项");
            Log("");

            int hits = r.Hit512 + r.Hit256;
            Log("判别位命中情况: 共 " + r.DiffTotal + " 个判别位，命中 " + hits + " 个（>=8 才敢下结论）");
            Log("");

            if (hits >= 8)
            {
                if (r.Hit512 > r.Hit256)
                {
                    Log("结论: 这个镜像是用【512MB 表】生成的 —— 给 512MB DDR3 的板子用。"
                        + "（置信 " + hits + "/" + r.DiffTotal + "）");
                    Status("检测结果：512MB DDR 配置");
                }
                else
                {
                    Log("结论: 这个镜像是用【256MB 表】生成的 —— 给 256MB DDR3 的板子用。"
                        + "（置信 " + hits + "/" + r.DiffTotal + "）");
                    Status("检测结果：256MB DDR 配置");
                }
            }
            else
            {
                Log("结论: 无法判定。");
                Log("    本工具生成的镜像一定是 " + r.DiffTotal + "/" + r.DiffTotal + "；命中这么少，说明这个 .imx");
                Log("    不是本工具生成的，多半是 NXP 官方 u-boot 的 .imx —— 它用 NXP 自己的");
                Log("    DCD 表，结构不同，不能据此判断 DDR 容量。");
                Log("    这种文件直接看文件名里的 ddr256 / ddr512 字样即可。");
                Status("检测结果：无法判定");
            }
            Log("");
        });
    }

    // ---------------- 烧写 ----------------

    private void btnBurn_Click(object sender, EventArgs e)
    {
        string bin = txtBin.Text.Trim();
        if (bin.Length == 0 || !File.Exists(bin))
        {
            MessageBox.Show(this, "请先选择一个存在的固件 .bin 文件。", "缺少固件",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ImxCore.DiskInfo d = cmbDisk.SelectedItem as ImxCore.DiskInfo;
        if (d == null)
        {
            MessageBox.Show(this, "请先选择目标磁盘（点「刷新」枚举后在下拉框里选）。", "缺少目标磁盘",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool use256 = rb256.Checked;

        string msg = "即将把固件写入物理磁盘：\r\n\r\n"
            + "    磁盘编号 : " + d.Index + "\r\n"
            + "    容量     : " + ImxCore.FormatSize(d.Size) + "\r\n"
            + "    类型     : " + d.TypeText + "\r\n"
            + "    盘符     : " + (d.Letters.Length > 0 ? d.Letters : "无") + "\r\n"
            + "    DDR 配置 : " + (use256 ? "256MB" : "512MB") + "\r\n"
            + "    固件     : " + Path.GetFileName(bin) + "\r\n\r\n"
            + "该磁盘上的所有分区和数据都会被破坏，不可恢复。\r\n"
            + "请再次确认：这确实是你开发板用的那张 SD 卡吗？";
        DialogResult r = MessageBox.Show(this, msg, "确认烧写",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes)
        {
            Log("已取消，未做任何写入。");
            return;
        }

        if (!d.IsRemovable)
        {
            string msg2 = "危险：这块盘被识别为【固定磁盘】，而不是可移动设备。\r\n\r\n"
                + "如果它不是 USB 读卡器，继续写入将毁掉里面的硬盘数据。\r\n\r\n"
                + "确实要写入磁盘 " + d.Index + "（" + ImxCore.FormatSize(d.Size) + "）吗？";
            DialogResult r2 = MessageBox.Show(this, msg2, "二次确认（固定磁盘）",
                MessageBoxButtons.YesNo, MessageBoxIcon.Stop, MessageBoxDefaultButton.Button2);
            if (r2 != DialogResult.Yes)
            {
                Log("已取消，未做任何写入。");
                return;
            }
        }

        int diskIndex = d.Index;
        RunAsync(delegate() { BurnCore(bin, diskIndex, use256); });
    }

    private void BurnCore(string binPath, int diskIndex, bool use256)
    {
        Log("========== 开始烧写 ==========");
        Log("固件    : " + Path.GetFullPath(binPath));
        Log("目标    : \\\\.\\PhysicalDrive" + diskIndex);
        Log("DDR 配置: " + (use256 ? "256MB" : "512MB"));
        Log("");

        byte[] bin = File.ReadAllBytes(binPath);
        Status("正在组装镜像 ...");
        byte[] image = ImxCore.BuildImage(bin, use256);
        Log("固件大小: " + bin.Length + " Bytes");
        Log("镜像大小: " + image.Length + " Bytes  (头部 3072 + 代码 " + bin.Length + "，与官方 imxdownload 一致)");
        Log("写入位置: 偏移 " + ImxCore.WriteOffset + " Bytes");
        Log("");

        Status("正在卸载目标盘上的卷 ...");
        Log("--- 卸载目标盘上的卷（防止系统缓存回写）---");
        ImxCore.DismountVolumes(diskIndex, new Action<string>(Log));
        Log("");

        Status("正在写入 ...");
        Log("--- 正在写入，请勿拔卡、勿断电 ---");
        string err;
        if (!ImxCore.WriteToDisk(diskIndex, image, out err))
        {
            Log("写入失败: " + err);
            Status("写入失败，详见日志。");
            Log("");
            return;
        }
        Log("写入完成。");
        Log("");

        Status("正在回读校验 ...");
        Log("--- 回读校验 ---");
        int diff;
        if (!ImxCore.VerifyOnDisk(diskIndex, image, out diff, out err))
        {
            Log("回读失败: " + err);
            Log("写入动作本身已经完成。建议拔掉读卡器重新插上，再用「检测 .imx」确认。");
            Status("回读失败，详见日志。");
            Log("");
            return;
        }

        if (diff == 0)
        {
            Log("校验通过：" + image.Length + " Bytes 与源数据完全一致。");
            Log("");
            Log("下一步：");
            Log("    1) 拔出 SD 卡，插到开发板上");
            Log("    2) 拨码开关切到 SD 卡启动");
            Log("    3) 上电，看串口/板子现象");
            if (!use256)
                Log("    提示：若串口毫无输出，很可能是 DDR 容量选错了 —— 改用 256MB 再烧一次试试。");
            Status("烧写成功，校验通过。");
        }
        else
        {
            Log("校验发现 " + diff + " 字节不一致，写入不完整，请重试。");
            Status("校验未通过，详见日志。");
        }
        Log("");
    }

    // ---------------- 入口 ----------------

    private const string ElevateMarker = "--elevated";
    private const string AllowFixedSwitch = "--allow-fixed";

    [STAThread]
    private static void Main(string[] args)
    {
        bool alreadyTried = false;
        foreach (string a in args)
        {
            if (a == ElevateMarker) alreadyTried = true;
            if (a == AllowFixedSwitch) MainForm.AllowFixed = true;
        }

        // 启动就提权：烧写要打开 \\.\PhysicalDriveN，普通用户会被拒（Win32 错误码 5）。
        // 放在 Application.Run 之前做，所以还没建窗口，不会闪一下再消失。
        // alreadyTried 是保险：万一提权后仍然不是管理员（UAC 被组策略限制等），
        // 不会再重新拉起一次，避免循环弹 UAC。
        if (!ImxCore.IsAdministrator() && !alreadyTried && TryRestartElevated(args))
            return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    // 用 ShellExecute 的 "runas" 动词把自己重新拉起一次（会弹 UAC）。
    // 返回 true 表示已成功交给提权后的新进程，本进程应当退出。
    //
    // 注意：必须把原来的命令行参数一起带过去，否则 --allow-fixed 之类的开关
    // 会在提权重启后丢失（子进程变成一个干净的进程）。每个参数都加引号，
    // 避免带空格的路径被拆开。
    private static bool TryRestartElevated(string[] args)
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                sb.Append('"');
                sb.Append(a);
                sb.Append("\" ");
            }
            sb.Append(ElevateMarker);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = Application.ExecutablePath;
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WorkingDirectory = Environment.CurrentDirectory;
            psi.Arguments = sb.ToString();
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            // 用户在 UAC 弹窗上点了「否」，或者程序在不能提权的路径下运行。
            MessageBox.Show(
                "没有拿到管理员权限，程序会以普通权限继续运行。\r\n\r\n"
                + "「检测 .imx 的 DDR 表」和「只生成镜像文件」仍然可用；\r\n"
                + "但「开始烧写」会因为打不开物理磁盘而失败（Win32 错误码 5）。\r\n\r\n"
                + "需要烧写时，请关掉本窗口，右键 exe →「以管理员身份运行」。\r\n\r\n"
                + "（细节：" + ex.Message + "）",
                "未获得管理员权限", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }
}
