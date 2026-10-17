using System.Collections.Generic;
using System.IO;

namespace Bbu.Core.Services;

/// <summary>
/// 命令行拼装的唯一出处。
/// 但**以引擎自己的文档为准**：2026-10-07 在 UnRAR 7.30.1 与 Rar 7.23 上逐条复测过。
/// 三条纪律：列表传参不经 shell；<c>-p</c> 与密码连成一个参数；不给密码时显式传 <c>-p-</c>。
///
/// 两处开关都是为了拿真实进度：
/// 7z 用 <c>-bsp1</c>；UnRAR 用 <c>-idc</c>（<c>-idq</c> 抑制
/// **整个 stdout**，而 UnRAR 把结构失败写在 stdout 上，判读因此长期失效）。
/// </summary>
public static class EngineCommand
{
    /// <summary>「不要再问密码」。不给密码时必须显式带上，否则引擎会读 stdin 等人输入。</summary>
    public const string NoPasswordArgument = "-p-";

    /// <summary>7-Zip 的字符集开关：固定 UTF-8，让输出与宿主代码页脱钩。</summary>
    public const string Utf8Output = "-sccUTF-8";

    /// <summary>
    /// 密码参数。<c>-p</c> 与密码必须拼成一个参数；空串得到裸 <c>-p</c>，那是「请提示我输入密码」，
    /// 所以空密码由调用方改派 7-Zip。
    /// </summary>
    public static IReadOnlyList<string> Password(string? password) =>
        password is null ? [NoPasswordArgument] : ["-p" + password];

    /// <summary>
    /// 密码含双引号、换行或 NUL 时命令行无法可靠传递，这种包应提示用户手解；判据只认这三个字符。
    /// NUL 那一个让命令行在它出现的位置截断，引擎连归档名都收不到。
    /// </summary>
    public static bool NeedsManual(string? password) =>
        !string.IsNullOrEmpty(password)
        && (password.Contains('"') || password.Contains('\n') || password.Contains('\0'));

    /// <summary>解压。7z 用 <c>-o</c> 指定输出目录，UnRAR 把目录当位置参数（末尾那个分隔符不能省）；同名已存在时开关按引擎各取一对。</summary>
    public static IReadOnlyList<string> Extract(
        EngineKind kind,
        string exe,
        string archive,
        string outdir,
        string? password,
        bool overwrite = true)
    {
        List<string> cmd = [exe, "x"];
        if (kind is EngineKind.SevenZip)
        {
            cmd.Add("-o" + outdir);
            cmd.Add("-y");
            cmd.Add(overwrite ? "-aoa" : "-aos");
            cmd.Add("-bso0");
            cmd.Add("-bsp1");
            cmd.Add(Utf8Output);
        }
        else
        {
            cmd.Add(overwrite ? "-o+" : "-o-");
            cmd.Add("-y");
            cmd.Add("-idc");
            // UnRAR 的 `--` 管不住 `@`：不关掉 listfile 解析，条目名以 `@` 开头时引擎会去读本地文件。
            cmd.Add("-@");
        }

        cmd.AddRange(Password(password));
        cmd.Add("--");
        cmd.Add(archive);
        if (kind is EngineKind.UnRar)
        {
            cmd.Add(outdir + Path.DirectorySeparatorChar);
        }

        return cmd;
    }

    /// <summary>
    /// 验证。给了 <paramref name="entry"/> 就只验证这一个条目，这是大包逐个试密码的关键优化。
    /// 单条目那一路 7z 不给 <c>-bso0</c>：条目名没匹配上时它仍返回 0 并打印
    /// <c>No files to process</c>，把输出藏起来会让这种情况变成「密码通过」的假阳性。
    /// </summary>
    public static IReadOnlyList<string> Test(
        EngineKind kind,
        string exe,
        string archive,
        string? password,
        string? entry = null)
    {
        List<string> cmd = [exe, "t"];
        if (kind is EngineKind.SevenZip)
        {
            cmd.Add("-y");
            if (entry is null)
            {
                cmd.Add("-bso0");
            }

            cmd.Add("-bsp1");
            cmd.Add(Utf8Output);
        }
        else
        {
            cmd.Add("-y");
            cmd.Add("-idc");
            cmd.Add("-@");
        }

        cmd.AddRange(Password(password));
        cmd.Add("--");
        cmd.Add(archive);
        if (entry is not null)
        {
            cmd.Add(entry);
        }

        return cmd;
    }

    /// <summary>列目录。<c>-slt</c> 是「一条记录一段」的机器可读格式，<c>-p-</c> 不试密码；两条 <c>List</c> 都只服务 7-Zip。</summary>
    public static IReadOnlyList<string> List(string exe, string archive) =>
        [exe, "l", "-slt", Utf8Output, NoPasswordArgument, "--", archive];

    /// <summary>
    /// 带密码列目录。文件名也加密的包用这一条验证密码：解密文件头就足以证明密码正确，
    /// 读入量是几十 KB；换成 <c>7z t</c> 会把整个包读一遍。
    /// </summary>
    public static IReadOnlyList<string> List(string exe, string archive, string? password) =>
        [exe, "l", "-slt", Utf8Output, .. Password(password), "--", archive];

    /// <summary>
    /// 拼成给人看的命令行。不做任何替换：<c>-p&lt;密码&gt;</c> 原样留着。这类行只走 debug
    /// 通道进日志文件，不往界面上灌：一次试密码能刷上百行，而且带着明文密码。
    /// </summary>
    public static string Display(IReadOnlyList<string> command) =>
        string.Join(" ", command);
}
