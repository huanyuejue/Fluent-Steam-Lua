using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SteamLuaManager.Services;

// 极简 Markdown 渲染：只覆盖 release notes 里实际会出现的语法，不追求规范完整。
// 输出 FlowDocument 交给 FlowDocumentScrollViewer 显示，让标题、粗体、代码、链接
// 有真实排版，而不是把标记剥掉变成一坨纯文本。
// 不支持表格、图片、嵌套列表——更新日志用不到，真出现了也退化成普通段落。
public static class MarkdownRenderer
{
    // 字体在渲染中反复用到，静态化避免每行都新建实例；BodyFont 对外暴露，
    // 让承载文档的控件复用同一份，避免各自再 new 一次
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    public static readonly FontFamily BodyFont = new("Microsoft YaHei");
    private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"\*\*([^*]+)\*\*", RegexOptions.Compiled);
    private static readonly Regex CodeRegex = new(@"`([^`]+)`", RegexOptions.Compiled);

    // 行内标记：粗体、代码、链接。先收集各类标记的区间，再按下标排序合并，
    // 这样一行里混排的样式才能正确呈现，多个正则也不会互相吃掉字符
    public static Paragraph BuildParagraph(string text, Brush primary, Brush secondary, Brush accent)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
        AppendInline(paragraph, text, primary, secondary, accent);
        return paragraph;
    }

    private static void AppendInline(Paragraph paragraph, string text, Brush primary, Brush secondary, Brush accent)
    {
        var spans = new List<(int Start, int Length, Inline Inline)>();
        foreach (Match m in LinkRegex.Matches(text))
        {
            var run = new Run(m.Groups[1].Value)
            {
                Foreground = accent,
                TextDecorations = TextDecorations.Underline,
                // 链接只做视觉呈现，点击跳转由持有方决定，这里不假设有导航能力
                ToolTip = m.Groups[2].Value
            };
            spans.Add((m.Index, m.Length, run));
        }
        foreach (Match m in BoldRegex.Matches(text))
            spans.Add((m.Index, m.Length, new Run(m.Groups[1].Value) { FontWeight = FontWeights.SemiBold, Foreground = primary }));
        foreach (Match m in CodeRegex.Matches(text))
            spans.Add((m.Index, m.Length, new Run(m.Groups[1].Value) { FontFamily = MonoFont, Foreground = secondary }));

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var cursor = 0;
        foreach (var (start, length, inline) in spans)
        {
            // 区间重叠的直接跳过：先到者优先，避免同一段文字被渲染两遍
            if (start < cursor) continue;
            if (start > cursor)
                paragraph.Inlines.Add(new Run(text[cursor..start]) { Foreground = primary });
            paragraph.Inlines.Add(inline);
            cursor = start + length;
        }
        if (cursor < text.Length)
            paragraph.Inlines.Add(new Run(text[cursor..]) { Foreground = primary });
    }

    // 主入口：Markdown 转 FlowDocument。颜色由调用方按主题传入的取色函数决定，
    // 渲染器本身不依赖窗口资源，方便复用与测试
    public static FlowDocument Render(string markdown, double baseFontSize = 13)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = baseFontSize,
            // FlowDocument 不继承窗口上的 TextOptions，也不继承全局 TextBlock 样式：
            // 不显式指定就会落到 WPF 默认的 Segoe UI + Ideal 渲染，中文回退字形发虚、小字号带锯齿。
            // 这里对齐项目全局设置（微软雅黑 + Display + 灰度抗锯齿）
            FontFamily = BodyFont,
            // 换行由容器宽度决定，别让文档自己撑出横向滚动
            ColumnWidth = double.PositiveInfinity
        };
        TextOptions.SetTextFormattingMode(doc, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(doc, TextRenderingMode.Grayscale);
        var primary = ResolveBrush("TextFillColorPrimaryBrush", Colors.Black);
        var secondary = ResolveBrush("TextFillColorSecondaryBrush", Colors.Gray);
        var accent = ResolveBrush("AccentTextFillColorPrimaryBrush", Colors.DodgerBlue);

        if (string.IsNullOrWhiteSpace(markdown)) return doc;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.Length == 0)
            {
                // 空行留一点视觉间隔，但不叠加（连续空行只算一次）
                doc.Blocks.Add(new Paragraph { Margin = new Thickness(0, 0, 0, 4) });
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                doc.Blocks.Add(new Paragraph(new Run(string.Empty))
                {
                    Margin = new Thickness(0, 6, 0, 10),
                    BorderBrush = secondary,
                    BorderThickness = new Thickness(0, 0, 0, 1)
                });
                continue;
            }

            if (trimmed[0] == '#')
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                var title = trimmed[level..].Trim();
                if (title.Length == 0) continue;
                var size = level switch
                {
                    1 => baseFontSize + 6,
                    2 => baseFontSize + 3,
                    3 => baseFontSize + 1,
                    _ => baseFontSize
                };
                doc.Blocks.Add(new Paragraph(new Run(title) { Foreground = primary })
                {
                    FontSize = size,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 8, 0, 6)
                });
                continue;
            }

            if (trimmed[0] == '>')
            {
                var quoted = trimmed.TrimStart('>').Trim();
                if (quoted.Length == 0) continue;
                var p = BuildParagraph(quoted, secondary, secondary, accent);
                p.Margin = new Thickness(0, 2, 0, 6);
                p.BorderBrush = secondary;
                p.BorderThickness = new Thickness(2, 0, 0, 0);
                p.Padding = new Thickness(10, 0, 0, 0);
                doc.Blocks.Add(p);
                continue;
            }

            var indent = line.Length - trimmed.Length;
            if (trimmed.Length > 1 && trimmed[1] == ' ' && (trimmed[0] == '-' || trimmed[0] == '*'))
            {
                var item = trimmed[2..].Trim();
                var p = BuildParagraph("•  " + item, primary, secondary, accent);
                p.Margin = new Thickness(indent / 2 * 12, 0, 0, 3);
                doc.Blocks.Add(p);
                continue;
            }

            doc.Blocks.Add(BuildParagraph(trimmed, primary, secondary, accent));
        }
        return doc;
    }

    // 优先取主题资源，取不到时退回固定色，保证没有 Application 资源也能渲染
    private static Brush ResolveBrush(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
        }
        catch { }
        return new SolidColorBrush(fallback);
    }
}
