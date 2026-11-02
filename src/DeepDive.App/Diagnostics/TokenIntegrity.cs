using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DeepDive.App.Diagnostics;

/// <summary>
/// 读当前进程访问令牌的完整性级别。
/// </summary>
/// <remarks>
/// 这是判断「为什么明明有权限却写不进去」最直接的一个数：UAC 过滤后的管理员令牌是
/// 中等完整性（0x2000），对着 Program Files 这类目录 ACL 看着全对也照样拒绝访问。
/// <para>
/// 不能靠 <see cref="WindowsIdentity.Groups"/> 里翻 <c>S-1-16-*</c>——本机实测那个列表
/// 里就没有完整性 SID（只有 10 个常规组），所以必须走 API 直接问令牌。
/// </para>
/// </remarks>
internal static class TokenIntegrity
{
    /// <summary>TOKEN_INFORMATION_CLASS.TokenIntegrityLevel</summary>
    private const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int informationClass,
        IntPtr information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, int subAuthority);

    /// <summary>
    /// 当前进程是不是跑在低完整性（沙箱 / AppContainer）里。
    /// </summary>
    /// <remarks>
    /// 这一条决定了很多事，尤其是浏览器：低完整性进程写不了
    /// <c>%LOCALAPPDATA%</c> 下浏览器自己的配置目录，Edge/Chrome 会因为
    /// 「建不了 Profile 锁文件」直接 abort，进程起来就死。
    /// </remarks>
    public static bool IsSandboxed
    {
        get
        {
            try
            {
                return TryReadRid(out var rid) && rid < 0x2000;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>一句人话，认不出来就说不知道，不编。</summary>
    public static string Describe()
    {
        try
        {
            if (!TryReadRid(out var rid)) return "完整性未知";

            return rid switch
            {
                0x1000 => "低完整性",          // 4096，AppContainer / 沙箱
                0x2000 => "中等完整性",        // 8192，UAC 过滤后的常态
                0x3000 => "高完整性（已提升）", // 12288
                0x4000 => "系统完整性",        // 16384
                _ => $"完整性 0x{rid:X}",
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return "完整性未知";
        }
    }

    /// <summary>取完整性 SID 的最后一段 RID（4096 / 8192 / 12288 / 16384）。</summary>
    private static bool TryReadRid(out int rid)
    {
        rid = 0;

        using var identity = WindowsIdentity.GetCurrent();
        var token = identity.Token;

        // 先问长度：必然失败并回 ERROR_INSUFFICIENT_BUFFER，size 就是所需字节数
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
        if (size <= 0) return false;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) return false;

            var label = Marshal.PtrToStructure<SidAndAttributes>(buffer);
            if (label.Sid == IntPtr.Zero) return false;

            var countPtr = GetSidSubAuthorityCount(label.Sid);
            if (countPtr == IntPtr.Zero) return false;

            var count = Marshal.ReadByte(countPtr);
            if (count == 0) return false;

            var lastPtr = GetSidSubAuthority(label.Sid, count - 1);
            if (lastPtr == IntPtr.Zero) return false;

            rid = Marshal.ReadInt32(lastPtr);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
