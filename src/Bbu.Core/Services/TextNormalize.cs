namespace Bbu.Core.Services;

/// <summary>
/// 文本归一：与包、密码、产物都无关的字符级改写。
///
/// 放在这里是因为它的消费者跨层：<c>NamePassword.Variants</c> 与 <c>OutputNaming.NormalizeForMatch</c>
/// 都要用它，挂到任何一边都会让那一层的职责多出「文本归一」这一项（欠账表 `debt 81`）。
/// </summary>
internal static class TextNormalize
{
    /// <summary>全角转半角。取值看起来正确却解不开时，常见原因是全角字符或空白混排。</summary>
    internal static string ToHalfWidth(string value)
    {
        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var code = value[i];
            buffer[i] = code switch
            {
                '\u3000' => ' ',
                >= '\uff01' and <= '\uff5e' => (char)(code - 0xfee0),
                _ => code,
            };
        }

        return new string(buffer);
    }
}
