# Draws the CardboardBox logo and writes:
#   app\CardboardBox.ico         (the .exe icon: 16/24/32/48/64/256 px)
#   server\server-icon.png       (the default 64x64 Minecraft server icon)
# Run from the repo root:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class BoxArt
{
    static PointF P(float x, float y, float s) { return new PointF(x * s / 256f, y * s / 256f); }

    public static Bitmap Draw(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        float s = size;
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            var top   = new[] { P(128, 22, s), P(236, 76, s), P(128, 130, s), P(20, 76, s) };
            var left  = new[] { P(20, 76, s), P(128, 130, s), P(128, 240, s), P(20, 186, s) };
            var right = new[] { P(128, 130, s), P(236, 76, s), P(236, 186, s), P(128, 240, s) };

            using (var b = new SolidBrush(Color.FromArgb(0xDE, 0xB2, 0x78))) g.FillPolygon(b, top);
            using (var b = new SolidBrush(Color.FromArgb(0xC4, 0x8E, 0x55))) g.FillPolygon(b, left);
            using (var b = new SolidBrush(Color.FromArgb(0xA5, 0x72, 0x3F))) g.FillPolygon(b, right);

            // packing tape across the top and down the front
            var tapeTop = new[] { P(63, 54, s), P(85, 44, s), P(193, 98, s), P(171, 108, s) };
            using (var b = new SolidBrush(Color.FromArgb(0xF1, 0xDB, 0xB0))) g.FillPolygon(b, tapeTop);
            var tapeFront = new[] { P(171, 108, s), P(193, 98, s), P(193, 144, s), P(171, 154, s) };
            using (var b = new SolidBrush(Color.FromArgb(0xD9, 0xBE, 0x8C))) g.FillPolygon(b, tapeFront);

            // flap seam
            using (var pen = new Pen(Color.FromArgb(0x8A, 0x5E, 0x32), Math.Max(1f, s / 64f)))
                g.DrawLine(pen, P(74, 49, s), P(182, 103, s));

            // server status lights on the left face
            if (size >= 24)
            {
                float r = Math.Max(1.5f, s * 9f / 256f);
                var leds = new[] { P(46, 150, s), P(70, 162, s), P(94, 174, s) };
                var colors = new[] { Color.FromArgb(0x4C, 0xD9, 0x64), Color.FromArgb(0x4C, 0xD9, 0x64), Color.FromArgb(0xF5, 0xC2, 0x42) };
                for (int i = 0; i < leds.Length; i++)
                    using (var b = new SolidBrush(colors[i])) g.FillEllipse(b, leds[i].X - r, leds[i].Y - r, r * 2, r * 2);
            }

            // outline
            using (var pen = new Pen(Color.FromArgb(0x5A, 0x3C, 0x1E), Math.Max(1f, s * 7f / 256f)) { LineJoin = LineJoin.Round })
            {
                g.DrawPolygon(pen, new[] { P(128, 22, s), P(236, 76, s), P(236, 186, s), P(128, 240, s), P(20, 186, s), P(20, 76, s) });
                g.DrawLine(pen, P(20, 76, s), P(128, 130, s));
                g.DrawLine(pen, P(128, 130, s), P(236, 76, s));
                g.DrawLine(pen, P(128, 130, s), P(128, 240, s));
            }
        }
        return bmp;
    }

    static byte[] Png(Bitmap b)
    {
        using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    // Classic 32-bit DIB entry (best compatibility for small sizes)
    static byte[] Dib(Bitmap b)
    {
        int w = b.Width, h = b.Height;
        using (var ms = new MemoryStream())
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(40); bw.Write(w); bw.Write(h * 2); bw.Write((short)1); bw.Write((short)32);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            for (int y = h - 1; y >= 0; y--)
                for (int x = 0; x < w; x++)
                {
                    var c = b.GetPixel(x, y);
                    bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                }
            int maskStride = ((w + 31) / 32) * 4;
            bw.Write(new byte[maskStride * h]); // AND mask unused with alpha
            return ms.ToArray();
        }
    }

    public static void WriteIco(string path, int[] sizes)
    {
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var b = Draw(sizes[i])) images[i] = sizes[i] >= 256 ? Png(b) : Dib(b);

        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((short)1); bw.Write((short)32);
                bw.Write(images[i].Length); bw.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
        }
    }

    public static void WritePng(string path, int size)
    {
        using (var b = Draw(size)) b.Save(path, ImageFormat.Png);
    }
}
'@

New-Item -ItemType Directory -Force (Join-Path $root 'app') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $root 'server') | Out-Null
[BoxArt]::WriteIco((Join-Path $root 'app\CardboardBox.ico'), @(16, 24, 32, 48, 64, 256))
[BoxArt]::WritePng((Join-Path $root 'server\server-icon.png'), 64)
[BoxArt]::WritePng((Join-Path $root 'tools\logo-256.png'), 256)
Write-Host 'Icons written.'
