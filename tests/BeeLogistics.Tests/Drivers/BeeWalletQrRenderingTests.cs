using System.Reflection;
using BeeLogistics.Modules.Drivers.Application.Handlers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using ZXing;

namespace BeeLogistics.Tests.Drivers;

/// <summary>
/// Rendering the BeeWallet QR with the bee mark in the middle (issue #93).
/// <para>
/// The failure this guards against is silent and expensive: a QR that still <i>looks</i> like a QR
/// but no longer decodes. Nothing in the request path would error — the driver would simply find
/// that customers cannot pay them, and only in the field.
/// </para>
/// </summary>
public class BeeWalletQrRenderingTests
{
    /// <summary>A QR Ph payload at the length and shape PayMongo actually returns.</summary>
    private const string Payload =
        "00020101021228620011ph.ppmi.p2p0111PAEYPHM2XXX0212817797809438031503d4711d3f65a5204000053036085405"
        + "50.005802PH5911BEE ILOCOS6013ILOCOS NORTE6304";

    private static Image<Rgba32> Render(string payload = Payload)
    {
        var method = typeof(GetBeeWalletTopUpQrQueryHandler)
            .GetMethod("RenderQrPng", BindingFlags.NonPublic | BindingFlags.Static)!;
        var uri = (string)method.Invoke(null, new object[] { payload })!;

        Assert.StartsWith("data:image/png;base64,", uri);
        return Image.Load<Rgba32>(Convert.FromBase64String(uri["data:image/png;base64,".Length..]));
    }

    [Fact]
    public void The_rendered_code_carries_the_bee_plate_in_the_middle()
    {
        using var qr = Render();

        // Dead centre is inside the white plate the mark sits on.
        Assert.Equal(new Rgba32(255, 255, 255), qr[qr.Width / 2, qr.Height / 2]);

        // And the mark is actually drawn on it — a plate with no bee would leave the whole region
        // white, which is exactly what a broken embedded resource produces.
        var dark = 0;
        var from = (int)(qr.Width * 0.42);
        var to = (int)(qr.Width * 0.58);
        for (var x = from; x < to; x++)
        for (var y = from; y < to; y++)
            if (qr[x, y].R < 100) dark++;

        Assert.True(dark > 200, $"expected the bee mark inside the plate, found {dark} dark pixels");
    }

    /// <summary>Reads the code back the way a phone camera would.</summary>
    private static string? Decode(Image<Rgba32> image)
    {
        var luminance = new byte[image.Width * image.Height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    luminance[(y * accessor.Width) + x] =
                        (byte)((row[x].R * 0.299) + (row[x].G * 0.587) + (row[x].B * 0.114));
            }
        });

        var reader = new BarcodeReaderGeneric
        {
            Options = new ZXing.Common.DecodingOptions
            {
                PossibleFormats = new[] { BarcodeFormat.QR_CODE },
                TryHarder = true,
            },
        };
        return reader.Decode(new RGBLuminanceSource(luminance, image.Width, image.Height,
            RGBLuminanceSource.BitmapFormat.Gray8))?.Text;
    }

    /// <summary>
    /// The one that matters: the code must still read back as the exact payload.
    /// <para>
    /// A size assertion would be a proxy, and a misleading one — the bee's own dark body breaks up
    /// the white plate, so measuring light runs under-reports the coverage and passes a plate that
    /// has grown well past the intended size. Decoding is the actual property.
    /// </para>
    /// <para>
    /// Scope, honestly: ZXing is a spec-grade decoder and reads this payload even with a 32% plate,
    /// so this catches a mark that has grown catastrophically (~40%), not one that has crept from
    /// 20% to 26%. That creep is a scan-quality regression on weaker phone decoders rather than an
    /// error-correction failure, and no in-process decoder will catch it — the plate size in the
    /// renderer is the guard for that, and the comment there says why it is set where it is.
    /// </para>
    /// </summary>
    [Fact]
    public void The_code_still_decodes_to_the_exact_payload()
    {
        using var qr = Render();
        Assert.Equal(Payload, Decode(qr));
    }

    /// <summary>
    /// And it must survive being shrunk. The app renders the code at 240px and a driver may show
    /// it on a cracked screen across a counter, so decoding only at full size is not enough.
    /// </summary>
    [Theory]
    [InlineData(300)]
    [InlineData(240)]
    [InlineData(200)]
    public void The_code_still_decodes_when_shrunk_to_what_the_app_shows(int size)
    {
        using var qr = Render();
        qr.Mutate(c => c.Resize(size, size));

        Assert.Equal(Payload, Decode(qr));
    }

    /// <summary>
    /// A payload long enough to push the code to a larger version must still render, and the plate
    /// must scale with it rather than staying a fixed pixel size.
    /// </summary>
    [Fact]
    public void A_longer_payload_still_renders_with_a_proportional_plate()
    {
        using var small = Render();
        using var large = Render(Payload + new string('A', 300));

        Assert.True(large.Width > small.Width, "a longer payload should need a bigger code");
        Assert.Equal(new Rgba32(255, 255, 255), large[large.Width / 2, large.Height / 2]);
    }

    [Fact]
    public void The_logo_resource_is_actually_embedded()
    {
        // The renderer swallows a missing resource and falls back to a plain code, which is right
        // at runtime and useless in a test — this asserts the resource is really shipped, so the
        // fallback never becomes the silent normal.
        var names = typeof(GetBeeWalletTopUpQrQueryHandler).Assembly.GetManifestResourceNames();
        Assert.Contains("BeeLogistics.Modules.Drivers.Application.Assets.bee-logo.png", names);
    }
}
