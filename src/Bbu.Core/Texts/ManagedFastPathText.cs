namespace Bbu.Core.Texts;

/// <summary>
/// 7z 纯托管快路的**内部诊断**文案：解析失败的原因、判据给不出结论的原因、会话自述。
///
/// 放在这里是因为仓规只有一条：中文字面量的唯一出处是 <c>src\Bbu.Core\Texts</c>。
/// 带参数的那些做成方法（与 <c>PierceText</c> / <c>ScanText</c> 同形），
/// 插值原样留在方法体里，调用点只传值 —— 参数顺序搬错的空间就没有了。
/// </summary>
public static class ManagedFastPathText
{
    // ── 会话自述（ManagedSevenZipSession.Describe）────────────────────────────

    /// <summary>文件名也加密（7z 的 <c>-mhe=on</c>）。</summary>
    public const string EncodedHeader = "加密头";

    /// <summary>文件名明文（7z 的 <c>-mhe=off</c>）。</summary>
    public const string PlainHeader = "明文头";

    /// <summary>folder 没声明 unpacked CRC 时那一格。</summary>
    public const string NoCrc = "无";

    /// <summary>判据挑中的那个 folder 与它的规模。</summary>
    public static string JudgeFolder(object folder, object unpackedSize) =>
        $"判据 folder：{folder}，解压后 {unpackedSize} 字节，";

    /// <summary>头里声明的期望 CRC32（已渲染成 <c>0x…</c> 或「无」）。</summary>
    public static string ExpectedCrc(string crc) => $"期望 CRC={crc}，";

    /// <summary>链尾是 LZMA2 时补一句它的 prop 字节。</summary>
    public static string Lzma2Tail(object prop) => $"，链尾 LZMA2 prop=0x{prop:X2}";

    // ── 判据给不出结论的原因（ManagedJudgeOutcome）──────────────────────────

    /// <summary>空密码候选不进快路。与密码层那一个来源标签是同一个词，复用它、不另写一份。</summary>
    public const string EmptyPassword = VaultText.OriginEmptyPassword;

    /// <summary>密文长度不是正整数或超出预算。</summary>
    public static string CipherLengthInvalid(object size) => $"密文长度不合法（{size}）";

    /// <summary>密文没读满。</summary>
    public const string CipherTruncated = "密文读不全";

    /// <summary>AES 解密抛了。</summary>
    public const string AesFailed = "AES 解不开";

    /// <summary>folder 既没有 CRC 也没有可用长度，无从判。</summary>
    public const string NoUsableCrc = "头里没有可用的期望 CRC/长度";

    /// <summary>解出来的字节比声明的明长还短。</summary>
    public static string DeclaredTooLong(object unpackedSize, object written) =>
        $"声明的明长 {unpackedSize} 超过解密结果 {written}";

    public const string Lzma2LengthInvalid = "LZMA2 输入长度不合法";

    /// <summary>解到很浅的位置就撞碎结构：这一档才敢判「肯定不对」。</summary>
    public static string Lzma2StructFailed(object produced) =>
        $"LZMA2 结构当场不成立（已解出 {produced} 字节）";

    /// <summary>解过阈值才失败：回「不知道」，不判。</summary>
    public static string Lzma2FailedLate(object produced) => $"LZMA2 解到 {produced} 字节才失败";

    /// <summary>内容 CRC32 对不上：这个密码肯定不对。</summary>
    public static string CrcMismatch(object actual, object expected) =>
        $"CRC32 对不上（算出 0x{actual:X8}，头里写 0x{expected:X8}）";

    /// <summary>内容 CRC32 对得上：不能划掉，但也不等于密码对。</summary>
    public const string CrcMatch = "CRC32 对得上";

    /// <summary>判据自己抛了：一律回「不知道」，绝不当成密码错。</summary>
    public static string JudgeThrew(string typeName) => $"判据自己出错：{typeName}";

    // ── 7zAES ────────────────────────────────────────────────────────────────

    /// <summary>NumCyclesPower 是 0x3F 那种特例：不做 KDF，key 直接拼。</summary>
    public const string DirectKey = "（直接拼 key，无 KDF）";

    /// <summary>coder properties 里的轮数超出支持范围。</summary>
    public static string CyclesOutOfRange(object numCyclesPower) =>
        $"NumCyclesPower={numCyclesPower} 超出支持范围";

    // ── 头解析（游标 / 头读取 / 布局）────────────────────────────────────────

    public const string CursorAtEnd = "7z 头读到尾了";

    /// <summary>要读的字节比剩下的多。</summary>
    public const string CursorNeedMore = "7z 头要的字节比剩下的多";

    public const string CursorSkipTooFar = "7z 头要跳的字节比剩下的多";

    /// <summary>起始头不足 32 字节。</summary>
    public const string SignatureTooShort = "7z 签名头不足 32 字节";

    /// <summary>起始头自己的 CRC32 对不上。</summary>
    public const string StartHeaderCrcMismatch = "7z startHeaderCRC 对不上";

    /// <summary>StreamsInfo 段尾不是 kEnd。</summary>
    public static string StreamsInfoNotEnd(object type) => $"StreamsInfo 结尾是 0x{type:X}，不是 kEnd";

    /// <summary>pack stream 个数超出解析上限。</summary>
    public static string TooManyPackStreams(object count) => $"pack stream 个数超出上限：{count}";

    /// <summary>folder 个数超出解析上限。</summary>
    public static string TooManyFolders(object count) => $"folder 个数超出上限：{count}";

    public const string StreamSwitchUnsupported = "folder 段来自外部数据流（StreamSwitch），本判据不处理";

    /// <summary>coder 个数超出解析上限。</summary>
    public static string TooManyCoders(object count) => $"coder 个数超出上限：{count}";

    /// <summary>一个头里所有 folder 的 coder 累加量超出解析上限。</summary>
    public static string TooManyCodersTotal(object count) => $"coder 累计个数超出上限：{count}";

    public const string CoderReservedBits = "coder 主字节的保留位非零";

    public const string CoderIdTooLong = "coder id 超过 8 字节";

    /// <summary>folder 的入口 coder 输出流不是一个。</summary>
    public const string CoderOutputsNotOne = "coder 的输出流不是 1 个";

    public const string TooManyInStreams = "folder 的入口流太多";

    public const string InStreamsLessThanBonds = "入口流比 bond 还少";

    public const string BondInStreamIndexInvalid = "bond 的入口流下标不合法";

    public const string BondCoderIndexInvalid = "bond 的 coder 下标不合法";

    public const string PackStreamIndexInvalid = "pack stream 下标不合法";

    public const string NoMainOutputCoder = "找不出 folder 的主输出 coder";

    /// <summary>头几个字节不是 7z 签名。</summary>
    public const string NotSevenZip = "不是 7z 包";

    /// <summary>7z 主版本号不认识。</summary>
    public static string MajorVersionUnknown(object major) => $"7z 主版本 {major} 不认识";

    /// <summary>next header 的长度或偏移越出文件范围。</summary>
    public const string NextHeaderRangeInvalid = "7z next header 长度/偏移越界";

    /// <summary>next header 没读全。</summary>
    public const string NextHeaderTruncated = "7z next header 读不全";

    /// <summary>next header 自己的 CRC32 对不上。</summary>
    public const string NextHeaderCrcMismatch = "7z nextHeaderCRC 对不上";

    /// <summary>next header 首字节既不是 kHeader 也不是 kEncodedHeader。</summary>
    public static string NextHeaderKindUnknown(object first) =>
        $"next header 首字节 0x{first:X} 既不是 kHeader 也不是 kEncodedHeader";
}
