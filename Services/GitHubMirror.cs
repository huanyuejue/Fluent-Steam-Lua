namespace SteamLuaManager.Services;

// GitHub 访问国内加速：raw 文件与 release 包走公共 gh-proxy 前缀链，直连永远第一位，
// 镜像挂了自动退化成直连行为。
// 注意：jsDelivr 指望不上——SteamManifestCache_Pro 这种规模（几千分支+几千 tag），
// 它的包 loader 根本建不出来（包页面 0 请求、versions 接口 500），别再接回来。
internal static class GitHubMirror
{
    public const string DirectKey = "direct";

    // 前缀式镜像：按顺序尝试，全部失败才报错（长期不可用的及时剔除，保持表内都是活的）
    public static readonly string[] ReleaseAssetMirrorPrefixes =
    [
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://gh-proxy.org/",
    ];

    // 偏好值归一化：未知主机（含已下线的）回直连，保证下拉框与排序逻辑不出现空选中
    public static string NormalizePreferredHost(string? preferredHost)
    {
        if (string.IsNullOrWhiteSpace(preferredHost) || preferredHost.Equals(DirectKey, StringComparison.OrdinalIgnoreCase))
            return DirectKey;
        return ReleaseAssetMirrorPrefixes.Any(p => p.Contains(preferredHost, StringComparison.OrdinalIgnoreCase))
            ? preferredHost
            : DirectKey;
    }

    // 源显示名：直连与各镜像主机（测速与进度文案共用；镜像 URL 里含 raw 域名，必须前缀判断）
    public static string SourceDisplayName(string url)
    {
        if (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
            return "raw 直连";
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            return "GitHub 直连";
        var host = url.Split(["://"], 2, StringSplitOptions.None).Last().Split('/').First();
        return host;
    }

    // 是否直连源：raw 直连的 404 是权威的"不存在"，镜像不可能有源站没有的文件，可直接短路
    public static bool IsDirectUrl(string url) =>
        url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase);

    // 按用户偏好排序下载源：默认直连首位；选了镜像则该镜像首位、直连垫底，其余镜像居中。
    // 偏好值对不上已知镜像时回退默认顺序。
    public static List<string> OrderSources(string directUrl, string? preferredHost)
    {
        preferredHost = NormalizePreferredHost(preferredHost);
        string? preferredPrefix = null;
        if (!preferredHost.Equals(DirectKey, StringComparison.OrdinalIgnoreCase))
            preferredPrefix = ReleaseAssetMirrorPrefixes.FirstOrDefault(p =>
                p.Contains(preferredHost, StringComparison.OrdinalIgnoreCase));
        if (preferredPrefix == null)
        {
            var urls = new List<string> { directUrl };
            foreach (var prefix in ReleaseAssetMirrorPrefixes)
                urls.Add(prefix + directUrl);
            return urls;
        }
        var ordered = new List<string> { preferredPrefix + directUrl };
        foreach (var prefix in ReleaseAssetMirrorPrefixes)
            if (prefix != preferredPrefix)
                ordered.Add(prefix + directUrl);
        ordered.Add(directUrl);
        return ordered;
    }

    // release asset 下载源
    public static List<string> WithAssetMirrors(string downloadUrl, string? preferredHost = null) =>
        OrderSources(downloadUrl, preferredHost);

    // manifest raw 文件下载源（拼 raw 直链，不要拼 blob 页面）
    public static List<string> ManifestRawMirrors(string rawUrl, string? preferredHost = null) =>
        OrderSources(rawUrl, preferredHost);
}
