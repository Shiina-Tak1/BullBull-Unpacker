namespace Bbu.Core.Texts;

/// <summary>
/// RAR4 快路的**内部诊断**文案：会话自述，只在排查「这个包为什么没进快路」时读。
///
/// 与 <c>ManagedFastPathText</c> 同形、同理由：中文字面量的唯一出处是
/// <c>src\Bbu.Core\Texts</c>，带参数的那条做成方法。
/// </summary>
public static class Rar4FastPathText
{
    /// <summary>档案级加密（RA `-hp`）：MAIN_HEAD 之后连头链一起加密。</summary>
    public const string HeaderEncrypted = "档案级加密";

    /// <summary>文件级加密（RA `-p`）：判的是这一个条目的数据段。</summary>
    public static string JudgeEntry(object plainLength, object crc) =>
        $"判据条目：明文 {plainLength} 字节，期望 CRC=0x{crc:X8}";
}
