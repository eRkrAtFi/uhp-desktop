namespace QemuManager.Models;

/// <summary>Wraps one Services.NetworkInfo for the Networking page's own
/// read-only DataGrid (Phase 6).</summary>
public class NetworkListItem
{
    public NetworkInfo Info { get; }

    public NetworkListItem(NetworkInfo info) => Info = info;

    public string EnvironmentName => Info.EnvironmentName;
    public string EngineDisplayName => Info.EngineDisplayName;
    public string Mode => Info.Mode;
    public string GuestAddressText => Info.GuestAddress ?? "-";
    public string GatewayAddressText => Info.GatewayAddress ?? "-";
}
