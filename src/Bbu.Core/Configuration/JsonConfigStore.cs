using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Bbu.Core.Logging;
using Bbu.Core.Texts;
using IOPath = System.IO.Path;

namespace Bbu.Core.Configuration;

/// <summary>
/// <c>config.json</c> 的读写。容错与 1.1.0 一致：缺字段、类型不对、枚举值不认识，都只回落**那一个字段**的
/// 默认值；整份读不出来（JSON 坏了、被占用）才回落默认配置。两种情形都会在 <see cref="LastError"/> 里留一句原因。
/// 写盘走临时文件加替换，避免写一半断电留下半份 JSON。
/// 两侧都是手写的（读 <see cref="JsonDocument"/>、写 <see cref="Utf8JsonWriter"/>）：反射序列化在 NativeAOT 下
/// 报 IL2026 / IL3050，而这份文件的字节形状读侧与 1.1.0 都按这一形状解析，手写才逐字节可控。
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    // 缩进两格 + 中文不转 \uXXXX，同原先那两个 JsonSerializerOptions 设置
    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public JsonConfigStore(string path, IRunLog? log = null)
    {
        Path = path;
        _log = log;
    }

    public string Path { get; }

    /// <summary>可选日志出口：回落默认值时写一条 Warn（不写，用户分不清「这次用的是默认值」与「我的设置丢了」）。</summary>
    private readonly IRunLog? _log;

    /// <summary>上一次读或写出的问题；正常时为 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    public AppConfig Load()
    {
        LastError = null;
        var config = new AppConfig();
        if (!File.Exists(Path))
        {
            return config;
        }

        string text;
        try
        {
            text = File.ReadAllText(Path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            NoteFallback(ex.Message);
            return config;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            Apply(doc.RootElement, config);
        }
        catch (JsonException ex)
        {
            LastError = ex.Message;
            NoteFallback(ex.Message);
            return new AppConfig();
        }

        return config;
    }

    private void NoteFallback(string reason) => _log?.Problem(LogText.ConfigFallbackToDefaultsAt, reason);

    public bool Save(AppConfig config)
    {
        LastError = null;
        try
        {
            var dir = IOPath.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = Serialize(config);
            var tmp = $"{Path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    stream.Write(bytes, 0, bytes.Length);
                    // 与密码本同口径：写完刷盘，断电时不许留下半份配置（B-2026-207）
                    stream.Flush(true);
                }

                File.Move(tmp, Path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // 写不进去交回结论、不抛：有一个调用点是拖完列宽 700ms 后的定时器，
            // 异常从那里逃出去就是逃出 UI 事件（B-2026-152）。
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 手写序列化。键名与顺序都是对外契约（读侧按同一套键名取值），这里改名或换序就会读丢用户的配置。
    /// </summary>
    private static string Serialize(AppConfig config)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", config.Version);
            writer.WriteNumber("timeoutMinutes", config.TimeoutMinutes);
            writer.WriteNumber("maxDepth", config.MaxDepth);
            writer.WriteNumber("minFreeGb", config.MinFreeGb);
            writer.WriteBoolean("flattenSingleChild", config.FlattenSingleChild);
            writer.WriteBoolean("removeSource", config.RemoveSource);
            writer.WriteBoolean("removeIntermediate", config.RemoveIntermediate);
            writer.WriteBoolean("cleanDelete", config.CleanDelete);
            writer.WriteBoolean("scanAppended", config.ScanAppended);
            WriteEnum(writer, "folderScan", config.FolderScan);
            writer.WriteNumber("passwordWorkers", config.PasswordWorkers);
            WriteEnum(writer, "outputMode", config.OutputMode);
            writer.WriteString("outputDirectory", config.OutputDirectory);
            WriteEnum(writer, "conflict", config.Conflict);
            WriteStrings(writer, "excludedExtensions", config.ExcludedExtensions);
            writer.WriteString("language", config.Language);
            WriteNumbers(writer, "tableColumnWidths", config.TableColumnWidths);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// 同原先的 <c>JsonStringEnumConverter(CamelCase)</c>：认得的枚举写 camelCase 名字，
    /// 不认得的（配置里写数字串读回来的那种）按它的默认 <c>allowIntegerValues</c> 写成数字。
    /// </summary>
    private static void WriteEnum<TEnum>(Utf8JsonWriter writer, string name, TEnum value)
        where TEnum : struct, Enum
    {
        if (Enum.IsDefined(value))
        {
            writer.WriteString(name, JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
        }
        else
        {
            writer.WriteNumber(name, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        }
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteNumbers(Utf8JsonWriter writer, string name, IReadOnlyList<double>? values)
    {
        if (values is null)
        {
            writer.WriteNull(name);
            return;
        }

        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }

    private static void Apply(JsonElement root, AppConfig config)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        config.Version = GetInt(root, "version", config.Version);
        config.TimeoutMinutes = GetDouble(root, "timeoutMinutes", config.TimeoutMinutes);
        config.MaxDepth = GetInt(root, "maxDepth", config.MaxDepth);
        config.MinFreeGb = GetDouble(root, "minFreeGb", config.MinFreeGb);
        config.FlattenSingleChild = GetBool(root, "flattenSingleChild", config.FlattenSingleChild);
        config.RemoveSource = GetBool(root, "removeSource", config.RemoveSource);
        config.RemoveIntermediate = GetBool(root, "removeIntermediate", config.RemoveIntermediate);
        config.CleanDelete = GetBool(root, "cleanDelete", config.CleanDelete);
        config.ScanAppended = GetBool(root, "scanAppended", config.ScanAppended);
        config.FolderScan = GetEnum(root, "folderScan", config.FolderScan);
        config.PasswordWorkers = GetInt(root, "passwordWorkers", config.PasswordWorkers);
        config.OutputMode = GetEnum(root, "outputMode", config.OutputMode);
        config.OutputDirectory = GetString(root, "outputDirectory", config.OutputDirectory);
        config.Conflict = GetEnum(root, "conflict", config.Conflict);
        config.ExcludedExtensions = GetStringList(root, "excludedExtensions", config.ExcludedExtensions);
        config.Language = GetString(root, "language", config.Language);
        config.TableColumnWidths = GetDoubleList(root, "tableColumnWidths", config.TableColumnWidths);
    }

    private static bool TryGet(JsonElement root, string name, JsonValueKind kind, out JsonElement value) =>
        root.TryGetProperty(name, out value) && value.ValueKind == kind;

    private static string GetString(JsonElement root, string name, string fallback) =>
        TryGet(root, name, JsonValueKind.String, out var v) ? v.GetString()! : fallback;

    private static bool GetBool(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static int GetInt(JsonElement root, string name, int fallback) =>
        TryGet(root, name, JsonValueKind.Number, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    private static double GetDouble(JsonElement root, string name, double fallback) =>
        TryGet(root, name, JsonValueKind.Number, out var v) && v.TryGetDouble(out var d) ? d : fallback;

    private static TEnum GetEnum<TEnum>(JsonElement root, string name, TEnum fallback)
        where TEnum : struct, Enum =>
        TryGet(root, name, JsonValueKind.String, out var v)
        && Enum.TryParse<TEnum>(v.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static IReadOnlyList<string> GetStringList(
        JsonElement root, string name, IReadOnlyList<string> fallback) =>
        TryGet(root, name, JsonValueKind.Array, out var v)
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!).ToList()
            : fallback;

    private static IReadOnlyList<double> GetDoubleList(
        JsonElement root, string name, IReadOnlyList<double> fallback) =>
        TryGet(root, name, JsonValueKind.Array, out var v)
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number)
                .Select(x => x.GetDouble()).ToList()
            : fallback;
}
