using System.Security.Cryptography;
using System.Text;

namespace BetterLC;

internal sealed class Wardrobe
{
    private const int MaxOutfits = 50;
    private static readonly byte[] StatusOk = ProtoWire.VarintField(1, 1);

    private List<byte[]> outfits = [];
    private byte[] tree = [];

    private static string IdOf(byte[] outfit) => Convert.ToHexString(ProtoWire.GetBytes(outfit, 1));
    private string DefaultId => Convert.ToHexString(ProtoWire.GetBytes(tree, 2));

    private static byte[] Timestamp() => ProtoWire.VarintField(1, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    private static byte[] NewOutfit(string name)
    {
        var random = RandomNumberGenerator.GetBytes(16);
        byte[] uuid = [0x09, .. random[..8], 0x11, .. random[8..]];
        var name64 = name.Length > 64 ? name[..64] : name;
        return ProtoWire.Message((1, uuid), (2, Encoding.UTF8.GetBytes(name64)), (5, Timestamp()), (6, Timestamp()));
    }

    private void Normalize()
    {
        outfits = outfits.Where(o => ProtoWire.GetBytes(o, 1).Length > 0).Take(MaxOutfits).ToList();
        if (outfits.Count == 0) outfits.Add(NewOutfit("Default Outfit"));
        if (!outfits.Any(o => IdOf(o) == DefaultId))
            tree = [.. ProtoWire.GetRepeated(tree, 1).SelectMany(c => ProtoWire.Message((1, c))),
                    .. ProtoWire.Message((2, ProtoWire.GetBytes(outfits[0], 1)))];
    }

    public byte[] Active => outfits.FirstOrDefault(o => IdOf(o) == DefaultId) ?? outfits[0];

    public byte[] LoginFields() =>
        [.. outfits.SelectMany(o => ProtoWire.Message((10, o))), .. ProtoWire.Message((11, tree))];

    public byte[]? Apply(string method, byte[] input)
    {
        switch (method)
        {
            case "createoutfit":
            {
                if (outfits.Count >= MaxOutfits) return ProtoWire.VarintField(1, 2);
                var name = ProtoWire.GetString(input, 1);
                var outfit = NewOutfit(name.Length > 0 ? name : "Outfit");
                outfits.Add(outfit);
                return [.. StatusOk, .. ProtoWire.Message((2, outfit))];
            }
            case "updateoutfit":
            {
                var outfit = ProtoWire.GetBytes(input, 1);
                if (ProtoWire.GetBytes(outfit, 1).Length == 0 || outfit.Length > 65_536) return [];
                var index = outfits.FindIndex(o => IdOf(o) == IdOf(outfit));
                if (index >= 0) outfits[index] = outfit;
                else if (outfits.Count < MaxOutfits) outfits.Add(outfit);
                Normalize();
                return [.. StatusOk, .. ProtoWire.Message((2, outfit))];
            }
            case "selectoutfit":
                tree = ProtoWire.GetBytes(input, 1);
                Normalize();
                return StatusOk;
            case "deleteoutfit":
            {
                var id = Convert.ToHexString(ProtoWire.GetBytes(input, 1));
                outfits.RemoveAll(o => IdOf(o) == id);
                Normalize();
                return StatusOk;
            }
            default:
                return null;
        }
    }

    public static async Task<Wardrobe> LoadAsync(string path, CancellationToken token)
    {
        var wardrobe = new Wardrobe();
        try
        {
            if (File.Exists(path))
            {
                var data = await File.ReadAllBytesAsync(path, token);
                wardrobe.outfits = ProtoWire.GetRepeated(data, 1);
                wardrobe.tree = ProtoWire.GetBytes(data, 2);
            }
        }
        catch (InvalidDataException) { Console.Error.WriteLine("Saved outfits were unreadable; starting fresh."); }
        wardrobe.Normalize();
        return wardrobe;
    }

    public async Task SaveAsync(string path, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] data = [.. outfits.SelectMany(o => ProtoWire.Message((1, o))), .. ProtoWire.Message((2, tree))];
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, data, token);
        File.Move(temporary, path, overwrite: true);
    }
}
