using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ShiftWheel;

/// <summary>PDF rendering, OCR (TXT/DOCX) and image→PDF, all on built-in Windows APIs.</summary>
internal static class PdfTools
{
    // ───────────── PDF → images ─────────────

    public static async Task<string[]> RenderPagesAsync(string pdf, string target, IProgress<double>? p)
    {
        var doc = await LoadPdf(pdf);
        string dir = Path.GetDirectoryName(pdf)!;
        string name = Path.GetFileNameWithoutExtension(pdf);
        var outputs = new List<string>();

        for (uint i = 0; i < doc.PageCount; i++)
        {
            string baseName = doc.PageCount == 1 ? name : $"{name} - page {i + 1}";
            string pngPath = target == "png"
                ? Converter.UniquePath(dir, baseName, "png")
                : Path.Combine(Path.GetTempPath(), $"shiftwheel_{Guid.NewGuid():N}.png");

            await RenderPageToPng(doc, i, pngPath, 3000);

            if (target == "png") outputs.Add(pngPath);
            else
            {
                string final = Converter.UniquePath(dir, baseName, target);
                try
                {
                    var args = new List<string> { "-i", pngPath };
                    if (target == "jpg") args.AddRange(new[] { "-q:v", "2", "-pix_fmt", "yuvj420p" });
                    else if (target == "webp") args.AddRange(new[] { "-c:v", "libwebp", "-quality", "90" });
                    args.AddRange(new[] { "-frames:v", "1", "-update", "1", final });
                    await Converter.RunFfmpeg(args, null);
                }
                finally { TryDelete(pngPath); }
                outputs.Add(final);
            }
            p?.Report((i + 1.0) / doc.PageCount);
        }
        return outputs.ToArray();
    }

    static async Task<PdfDocument> LoadPdf(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            return await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex)
        {
            throw new ConvertException("Не удалось открыть PDF (возможно, он защищён паролем): " + ex.Message);
        }
    }

    static async Task RenderPageToPng(PdfDocument doc, uint index, string pngPath, uint maxWidth)
    {
        using var page = doc.GetPage(index);
        // PDF page size is in 1/96" units; render at ~2x for readable text, capped for huge pages.
        uint width = (uint)Math.Min(maxWidth, Math.Max(800, page.Size.Width * 2));
        var opts = new PdfPageRenderOptions
        {
            DestinationWidth = width,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, opts);
        stream.Seek(0);
        using var fs = File.Create(pngPath);
        await stream.AsStreamForRead().CopyToAsync(fs);
    }

    // ───────────── OCR → TXT / DOCX ─────────────

    static OcrEngine CreateOcr()
    {
        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? throw new ConvertException("В Windows не установлен язык для распознавания текста (Параметры → Язык и регион).");
    }

    public static async Task<string> OcrPdfAsync(string pdf, string target, IProgress<double>? p)
    {
        var doc = await LoadPdf(pdf);
        var engine = CreateOcr();
        var pages = new List<string>();
        string tmp = Path.Combine(Path.GetTempPath(), $"shiftwheel_{Guid.NewGuid():N}.png");
        try
        {
            for (uint i = 0; i < doc.PageCount; i++)
            {
                await RenderPageToPng(doc, i, tmp, 2600);
                pages.Add(await Recognize(engine, tmp));
                p?.Report((i + 1.0) / doc.PageCount);
            }
        }
        finally { TryDelete(tmp); }

        return WriteText(pdf, target, string.Join("\n\n", pages));
    }

    public static async Task<string> OcrImageAsync(string image, string target)
    {
        var engine = CreateOcr();
        string src = image;
        string? tmp = null;
        string ext = Formats.Ext(image);
        if (ext != "jpg")
        {
            // Normalise to an opaque PNG: transparent pixels would otherwise be read as black.
            tmp = Path.Combine(Path.GetTempPath(), $"shiftwheel_{Guid.NewGuid():N}.png");
            await Converter.RunFfmpeg(new[]
            {
                "-i", image,
                "-vf", "format=rgba,split[a][b];[a]drawbox=c=white:t=fill[bg];[bg][b]overlay=shortest=1,format=rgb24",
                "-frames:v", "1", "-update", "1", tmp,
            }, null);
            src = tmp;
        }
        try
        {
            string text = await Recognize(engine, src);
            return WriteText(image, target, text);
        }
        finally { if (tmp != null) TryDelete(tmp); }
    }

    static async Task<string> Recognize(OcrEngine engine, string imagePath)
    {
        var file = await StorageFile.GetFileFromPathAsync(imagePath);
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        uint max = OcrEngine.MaxImageDimension;
        SoftwareBitmap bmp;
        if (decoder.PixelWidth > max || decoder.PixelHeight > max)
        {
            double k = Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight);
            var tr = new BitmapTransform
            {
                ScaledWidth = (uint)(decoder.PixelWidth * k),
                ScaledHeight = (uint)(decoder.PixelHeight * k),
            };
            bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        }
        else
        {
            bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }

        var result = await engine.RecognizeAsync(bmp);
        return string.Join("\n", result.Lines.Select(l => l.Text));
    }

    static string WriteText(string source, string target, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ConvertException("Текст не найден");
        string dir = Path.GetDirectoryName(source)!;
        string name = Path.GetFileNameWithoutExtension(source);
        string output = Converter.UniquePath(dir, name, target);
        if (target == "txt") File.WriteAllText(output, text, new UTF8Encoding(true));
        else WriteDocx(output, text);
        return output;
    }

    // ───────────── minimal DOCX writer ─────────────

    static void WriteDocx(string path, string text)
    {
        static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        var body = new StringBuilder();
        foreach (var line in text.Replace("\r", "").Split('\n'))
            body.Append("<w:p><w:r><w:t xml:space=\"preserve\">").Append(Esc(line)).Append("</w:t></w:r></w:p>");

        string document =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            body + "</w:body></w:document>";
        const string contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "</Types>";
        const string rels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, string content)
        {
            var e = zip.CreateEntry(entry);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("[Content_Types].xml", contentTypes);
        Add("_rels/.rels", rels);
        Add("word/document.xml", document);
    }

    // ───────────── image → PDF ─────────────

    public static async Task<string> ImageToPdfAsync(string image)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"shiftwheel_{Guid.NewGuid():N}.jpg");
        try
        {
            await Converter.RunFfmpeg(new[]
            {
                "-i", image,
                "-vf", "format=rgba,split[a][b];[a]drawbox=c=white:t=fill[bg];[bg][b]overlay=shortest=1,format=yuvj420p",
                "-q:v", "2", "-frames:v", "1", "-update", "1", tmp,
            }, null);

            byte[] jpg = await File.ReadAllBytesAsync(tmp);
            string output = Converter.UniquePath(Path.GetDirectoryName(image)!, Path.GetFileNameWithoutExtension(image), "pdf");
            File.WriteAllBytes(output, BuildJpegPdf(jpg));
            return output;
        }
        finally { TryDelete(tmp); }
    }

    static (int W, int H, int Comps) JpegInfo(byte[] d)
    {
        int i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            int m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6], d[i + 9]);
            i += 2 + ((d[i + 2] << 8) | d[i + 3]);
        }
        throw new ConvertException("Не удалось разобрать JPEG");
    }

    static byte[] BuildJpegPdf(byte[] jpg)
    {
        var (w, h, comps) = JpegInfo(jpg);
        string cs = comps == 1 ? "/DeviceGray" : "/DeviceRGB";
        // 96 dpi → points
        string pw = (w * 0.75).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string ph = (h * 0.75).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string content = $"q {pw} 0 0 {ph} 0 0 cm /Im0 Do Q";

        using var ms = new MemoryStream();
        var offsets = new List<long>();
        void Text(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(ms.Position); Text(s); }

        Text("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw} {ph}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace {cs} /BitsPerComponent 8 /Filter /DCTDecode /Length {jpg.Length} >>\nstream\n");
        ms.Write(jpg, 0, jpg.Length);
        Text("\nendstream\nendobj\n");
        Obj($"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        long xref = ms.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Text($"{o:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
