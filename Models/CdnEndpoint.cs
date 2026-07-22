namespace SteamLuaManager.Models;

public class CdnEndpoint
{
    public string Name { get; init; }
    public string UrlTemplate { get; init; }
    public bool IsImageEndpoint { get; init; } = true;

    public CdnEndpoint(string name, string urlTemplate)
    {
        Name = name;
        UrlTemplate = urlTemplate;
    }

    public static List<CdnEndpoint> Defaults { get; } = new()
    {
        new("Store API (默认节点)", "https://store.steampowered.com/api/appdetails?appids={0}&l=schinese&filters=basic")
        { IsImageEndpoint = false },
        new("Akamai 主节点", "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{0}/header.jpg"),
        new("Akamai 备用", "https://cdn.akamai.steamstatic.com/steam/apps/{0}/header.jpg"),
        new("Cloudflare CDN", "https://cdn.cloudflare.steamstatic.com/steam/apps/{0}/header.jpg"),
        new("Akamai 大图", "https://cdn.akamai.steamstatic.com/steam/apps/{0}/library_600x900.jpg"),
    };

    public override string ToString() => Name;
}
