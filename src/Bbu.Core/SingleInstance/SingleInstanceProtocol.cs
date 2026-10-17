using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Bbu.Core.SingleInstance;

/// <summary>
/// 单实例协议。互斥体判定谁是主实例，管道把后来者的路径递进去；载荷形状沿用 1.1.0。
/// 互斥体用 <c>Local\</c> 前缀 = **每登录会话一个**：两个 Windows 账号同时在线时各成主实例，
/// 这是刻意的（改成 <c>Global\</c> 会让第二个会话把路径投进用户看不见的窗口）。管道名跟着同一个作用域走。
/// </summary>
public static class SingleInstanceProtocol
{
    public const string MutexName = @"Local\bbu-unpacker-single";

    /// <summary>管道名的前缀，完整的名字按登录会话派生。</summary>
    public const string PipeNamePrefix = "bbu-unpacker";

    /// <summary>连不上、写不进去、等不到回执，三步各用的上限（毫秒）。</summary>
    public const int ForwardTimeoutMs = 2000;

    /// <summary>单条消息的字节上限。协议就是「一批绝对路径加一个布尔」，1 MiB 够用。</summary>
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>主实例收下载荷之后回的那句话，转发方等到它才算递达。</summary>
    public const string Receipt = "ok";

    /// <summary>
    /// 一条消息的结尾。写侧写完补一个换行，读侧读到换行即返回，不得读到对端关闭：写完还要等回执，
    /// 两边互等即死锁。对端写完直接关闭也算一条。
    /// </summary>
    public const char MessageTerminator = '\n';

    /// <summary>建管道失败后的首次等待与封顶（毫秒）。</summary>
    public const int RetryFirstMs = 100;

    public const int RetryCapMs = 2000;

    /// <summary>
    /// 本进程要用的管道名，**按登录会话派生**，与互斥体同一个作用域。
    /// 不派生就有两条后果：第二个会话里的主实例建不起同名的管道，而它的转发会连上第一个会话的管道 ——
    /// 用户拖进去的文件出现在另一个窗口里。名字只有这一处产出。
    /// </summary>
    public static string PipeName { get; } = PipeNameFor(CurrentSessionId());

    public static string PipeNameFor(int sessionId) => $"{PipeNamePrefix}-s{sessionId}";

    public static int CurrentSessionId() => Process.GetCurrentProcess().SessionId;

    // 载荷只有两个字段，两侧都手写：反射序列化在 NativeAOT 下会报 IL2026 / IL3050
    // （失效即转发不出去），source-gen 又要多一层对不齐形状的余地。
    // 编码器是默认那份（**不是** UnsafeRelaxedJsonEscaping）：中文按 \uXXXX 转义是线上
    // 与 1.1.0 对接的字节形状，形状本身（camelCase 的 paths 数组 + auto 布尔）不许动。
    // 不缩进，同原先没设 WriteIndented 的那份 JsonSerializerOptions。
    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.Default,
    };

    public static string Serialize(LaunchPayload payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            writer.WriteStartObject();
            if (payload.Paths is null)
            {
                writer.WriteNull("paths");
            }
            else
            {
                writer.WriteStartArray("paths");
                foreach (var path in payload.Paths)
                {
                    writer.WriteStringValue(path);
                }

                writer.WriteEndArray();
            }

            writer.WriteBoolean("auto", payload.Auto);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 读回来的判据与原先的 <c>JsonSerializer.Deserialize&lt;LaunchPayload&gt;</c> 逐条对齐：
    /// 键名区分大小写、认不得的键跳过、同名键以最后一个为准、类型不对与整份坏掉都是"读不出来"，
    /// 而**缺字段不是**（缺 <c>paths</c> 时那份载荷的 <see cref="LaunchPayload.Paths"/> 就是 null）。
    /// </summary>
    public static bool TryDeserialize(string json, out LaunchPayload payload)
    {
        payload = LaunchPayload.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            IReadOnlyList<string>? paths = null;
            var auto = false;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.NameEquals("paths"))
                {
                    if (!TryReadPaths(property.Value, out paths))
                    {
                        return false;
                    }
                }
                else if (property.NameEquals("auto"))
                {
                    switch (property.Value.ValueKind)
                    {
                        case JsonValueKind.True:
                            auto = true;
                            break;
                        case JsonValueKind.False:
                            auto = false;
                            break;
                        default:
                            return false;
                    }
                }
            }

            payload = new LaunchPayload(paths!, auto);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadPaths(JsonElement value, out IReadOnlyList<string>? paths)
    {
        paths = null;
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var list = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                list.Add(null!);
                continue;
            }

            if (element.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            list.Add(element.GetString()!);
        }

        paths = list;
        return true;
    }
}

/// <summary>转发进来的载荷：一批路径 + 是否自动开跑。</summary>
public sealed record LaunchPayload(IReadOnlyList<string> Paths, bool Auto)
{
    public static LaunchPayload Empty { get; } = new(Array.Empty<string>(), false);
}
