namespace ServerManagerPro.Core;

/// <summary>Grobe Herkunft einer MAC-Adresse: zufällige/lokale Adressen und bekannte virtuelle Adapter. Eine vollständige Herstellerliste ist bewusst nicht enthalten.</summary>
public static class MacVendor
{
    static readonly Dictionary<string, string> Virtual = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00-50-56"] = "VMware", ["00-0C-29"] = "VMware", ["00-05-69"] = "VMware",
        ["00-15-5D"] = "Hyper-V", ["52-54-00"] = "QEMU/KVM", ["08-00-27"] = "VirtualBox", ["00-16-3E"] = "Xen",
    };

    public static string Lookup(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return "";
        var clean = mac.Trim().Replace(':', '-').ToUpperInvariant();
        if (clean.Length < 8) return "";
        if (Virtual.TryGetValue(clean[..8], out var v)) return v + " (virtuell)";
        if (int.TryParse(clean[..2], System.Globalization.NumberStyles.HexNumber, null, out int first) && (first & 0x02) != 0)
            return "zufällige/lokale Adresse (z. B. Smartphone mit privater MAC, VM, Container)";
        return "";
    }
}
