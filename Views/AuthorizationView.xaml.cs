using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using SteamLuaManager.ViewModels;

namespace SteamLuaManager.Views;

/// <summary>
/// 授权页：提取 + 导入 Denuvo 授权票据。
/// 拖放提示统一由窗口级遮罩负责；本页仅控制 Effects：票据文件放行（由窗口级导入），
/// 其他文件（如 .lua）置 None 阻止入库。
/// </summary>
public partial class AuthorizationView
{
    private AuthorizationViewModel? _viewModel;

    public AuthorizationView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => _viewModel = DataContext as AuthorizationViewModel;
    }

    private void DropZone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel?.BrowseImportCommand.Execute(null);
        e.Handled = true;
    }

    private void ExtractAppIdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel != null && _viewModel.ExtractCommand.CanExecute(null))
        {
            _viewModel.ExtractCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Zone_DragEnter(object sender, DragEventArgs e)
    {
        HandleDrag(e);
    }

    private void Zone_DragOver(object sender, DragEventArgs e)
    {
        HandleDrag(e);
    }

    private void HandleDrag(DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                    e.Data.GetData(DataFormats.FileDrop) is string[] f
            ? f
            : Array.Empty<string>();
        var isTicket = files.Length > 0 && files.Any(IsTicketFile);
        if (isTicket)
        {
            // 票据文件：放行到窗口级（统一遮罩 + 统一导入），不置 Handled
            e.Effects = DragDropEffects.Copy;
            return;
        }
        // 其他文件：阻止在授权页拖放，同时阻止冒泡（避免窗口级 .lua/.bin 遮罩与入库）
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>判断文件是否为授权票据文件（tickets.txt，或内容含票据标记的 .txt）。</summary>
    private static bool IsTicketFile(string path)
    {
        try
        {
            if (!path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(Path.GetFileNameWithoutExtension(path), "tickets",
                    StringComparison.OrdinalIgnoreCase))
                return true;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[1024];
            var n = fs.Read(buf, 0, buf.Length);
            if (n <= 0) return false;
            var head = Encoding.UTF8.GetString(buf, 0, n);
            return head.Contains("appid:", StringComparison.OrdinalIgnoreCase) ||
                   head.Contains("appticket", StringComparison.OrdinalIgnoreCase) ||
                   head.Contains("eticket", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void Zone_Drop(object sender, DragEventArgs e)
    {
        // 票据导入统一由窗口级 Window_Drop 处理；此处只阻止非票据文件的拖放
        if (!IsTicketDrop(e.Data))
            e.Handled = true;
    }

    private static bool IsTicketDrop(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        return data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsTicketFile);
    }
}