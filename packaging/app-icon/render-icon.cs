#:package Svg.Skia@5.2.3

// Renders icon.svg into the icon files the app and packages use. Run it after changing icon.svg:
//
//   dotnet run packaging/app-icon/render-icon.cs
//
// Writes, next to this file:
//   icon.ico   Windows: the .exe, its shortcuts, the window and Setup.exe (16 to 256 pixels)
//   icon.icns  macOS: the .app in Finder and the Dock, with the margin macOS icons have around them
//   icon.png   Linux: the AppImage (512 pixels)

using SkiaSharp;
using Svg.Skia;

var folder = Path.GetDirectoryName(Path.GetFullPath(AppContext.GetData("EntryPointFilePath") as string ?? "packaging/app-icon/render-icon.cs"))!;
using var svg = new SKSvg();
svg.Load(Path.Combine(folder, "icon.svg"));
var picture = svg.Picture ?? throw new InvalidOperationException("icon.svg didn't load.");

// Draws the icon on a transparent square. macOS icons sit inside a margin: the artwork is 824 of 1024 pixels.
byte[] Png(int size, bool macMargin = false)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    var artwork = macMargin ? size * 824f / 1024 : size;
    canvas.Translate((size - artwork) / 2, (size - artwork) / 2);
    canvas.Scale(artwork / picture.CullRect.Width, artwork / picture.CullRect.Height);
    canvas.DrawPicture(picture);
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// .ico: a directory of PNG images, which Windows Vista and later read.
int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var icoImages = icoSizes.Select(s => Png(s)).ToArray();
using (var ico = new BinaryWriter(File.Create(Path.Combine(folder, "icon.ico"))))
{
    ico.Write((short)0);
    ico.Write((short)1);
    ico.Write((short)icoSizes.Length);
    var offset = 6 + 16 * icoSizes.Length;
    for (var i = 0; i < icoSizes.Length; i++)
    {
        ico.Write((byte)(icoSizes[i] % 256)); // 0 means 256
        ico.Write((byte)(icoSizes[i] % 256));
        ico.Write((byte)0);
        ico.Write((byte)0);
        ico.Write((short)1);
        ico.Write((short)32);
        ico.Write(icoImages[i].Length);
        ico.Write(offset);
        offset += icoImages[i].Length;
    }
    foreach (var image in icoImages)
        ico.Write(image);
}

// .icns: big-endian chunks, each a type code and a PNG. ic11 to ic14 are the Retina (@2x) sizes.
(string Type, int Size)[] icnsSizes =
    [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
     ("ic11", 32), ("ic12", 64), ("ic13", 256), ("ic14", 512)];
var chunks = icnsSizes.Select(e => (e.Type, Data: Png(e.Size, macMargin: true))).ToArray();
using (var icns = new BinaryWriter(File.Create(Path.Combine(folder, "icon.icns"))))
{
    void BigEndian(int value) => icns.Write(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
    icns.Write("icns"u8);
    BigEndian(8 + chunks.Sum(c => 8 + c.Data.Length));
    foreach (var (type, data) in chunks)
    {
        icns.Write(System.Text.Encoding.ASCII.GetBytes(type));
        BigEndian(8 + data.Length);
        icns.Write(data);
    }
}

File.WriteAllBytes(Path.Combine(folder, "icon.png"), Png(512));
Console.WriteLine($"Wrote icon.ico, icon.icns and icon.png in {folder}");
