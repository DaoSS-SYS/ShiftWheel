using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ShiftWheel;

internal sealed class ConvertException : Exception
{
    public ConvertException(string message) : base(message) { }
}

internal static class Converter
{
    // Flattens transparency onto white (needed for JPG/BMP/PDF), then converts to full-range YUV for JPEG.
    const string FlattenWhite = "format=rgba,split[a][b];[a]drawbox=c=white:t=fill[bg];[bg][b]overlay=shortest=1";
    const string PaletteChain = "split[s0][s1];[s0]palettegen=max_colors=256[p];[s1][p]paletteuse=dither=bayer";

    /// <param name="action">"fmt:PNG" for a conversion, "tool:compress|strip|resize|rotate" for a tool.</param>
    public static async Task<string[]> RunAsync(string input, string action, IProgress<double>? progress = null)
    {
        if (!File.Exists(input)) throw new ConvertException("Файл не найден");
        var kind = Formats.Classify(input);
        if (kind == FileKind.Unknown) throw new ConvertException("Этот тип файла не поддерживается");

        var parts = action.Split(':', 2);
        if (parts[0] == "fmt")
            return await ToFormat(input, kind, parts[1].ToLowerInvariant(), progress);
        return new[] { await Tool(input, kind, parts[1], progress) };
    }

    // ───────────────────────────── conversions ─────────────────────────────

    static async Task<string[]> ToFormat(string input, FileKind kind, string target, IProgress<double>? p)
    {
        switch (kind)
        {
            case FileKind.Image:
                if (target is "txt" or "docx") return new[] { await PdfTools.OcrImageAsync(input, target) };
                if (target == "pdf") return new[] { await PdfTools.ImageToPdfAsync(input) };
                if (target == "mp4") return new[] { await Ffmpeg(input, target, GifToMp4Args(), p) };
                return new[] { await Ffmpeg(input, target, ImageArgs(target), p) };

            case FileKind.Pdf:
                if (target is "txt" or "docx") return new[] { await PdfTools.OcrPdfAsync(input, target, p) };
                return await PdfTools.RenderPagesAsync(input, target, p);

            case FileKind.Video:
                return new[] { await VideoTo(input, target, p) };

            case FileKind.Audio:
                return new[] { await Ffmpeg(input, target, AudioArgs(target), p) };

            case FileKind.Word:
            case FileKind.Slides:
            case FileKind.Sheet:
                return await OfficeTools.ConvertAsync(input, kind, target, p);
        }
        throw new ConvertException("Неподдерживаемая операция");
    }

    static List<string> ImageArgs(string ext)
    {
        var a = new List<string>();
        switch (ext)
        {
            case "jpg":
                a.AddRange(new[] { "-vf", FlattenWhite + ",format=yuvj420p", "-q:v", "2" });
                break;
            case "bmp":
                a.AddRange(new[] { "-vf", FlattenWhite + ",format=bgr24" });
                break;
            case "webp":
                a.AddRange(new[] { "-c:v", "libwebp", "-quality", "90" });
                break;
            case "ico":
                a.AddRange(new[] { "-vf", "scale='min(256,iw)':'min(256,ih)':force_original_aspect_ratio=decrease,format=bgra" });
                break;
        }
        a.AddRange(new[] { "-frames:v", "1", "-update", "1" });
        return a;
    }

    static List<string> GifToMp4Args() => new()
    {
        "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2,format=yuv420p",
        "-c:v", "libx264", "-crf", "20", "-movflags", "+faststart", "-an",
    };

    static List<string> AudioArgs(string ext) => ext switch
    {
        "mp3" => new() { "-vn", "-c:a", "libmp3lame", "-q:a", "2" },
        "wav" => new() { "-vn", "-c:a", "pcm_s16le" },
        "flac" => new() { "-vn", "-c:a", "flac" },
        "ogg" => new() { "-vn", "-c:a", "libvorbis", "-q:a", "5" },
        "m4a" => new() { "-vn", "-c:a", "aac", "-b:a", "192k" },
        "aac" => new() { "-vn", "-c:a", "aac", "-b:a", "192k" },
        "opus" => new() { "-vn", "-c:a", "libopus", "-b:a", "128k" },
        _ => new() { "-vn" },
    };

    static async Task<string> VideoTo(string input, string target, IProgress<double>? p)
    {
        switch (target)
        {
            case "mp4":
                return await Ffmpeg(input, target, new()
                {
                    "-c:v", "libx264", "-crf", "22", "-preset", "medium", "-pix_fmt", "yuv420p",
                    "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
                    "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart",
                }, p);
            case "mkv":
            case "mov":
                // Remux is instant and lossless; fall back to a re-encode if the codecs don't fit.
                try { return await Ffmpeg(input, target, new() { "-c", "copy" }, p); }
                catch (ConvertException)
                {
                    return await Ffmpeg(input, target, new()
                    {
                        "-c:v", "libx264", "-crf", "22", "-pix_fmt", "yuv420p",
                        "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:a", "aac", "-b:a", "160k",
                    }, p);
                }
            case "webm":
                return await Ffmpeg(input, target, new()
                {
                    "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k",
                }, p);
            case "avi":
                return await Ffmpeg(input, target, new()
                {
                    "-c:v", "mpeg4", "-q:v", "4", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:a", "libmp3lame", "-q:a", "3",
                }, p);
            case "gif":
                return await Ffmpeg(input, target, new()
                {
                    "-an", "-vf", "fps=12,scale='min(480,iw)':-1:flags=lanczos," + PaletteChain, "-loop", "0",
                }, p);
            case "mp3": case "wav": case "m4a":
                return await Ffmpeg(input, target, AudioArgs(target), p);
        }
        throw new ConvertException($"Формат {target.ToUpperInvariant()} для видео не поддерживается");
    }

    // ───────────────────────────── tools ─────────────────────────────

    static async Task<string> Tool(string input, FileKind kind, string tool, IProgress<double>? p)
    {
        string ext = Formats.Ext(input);
        if (kind is not (FileKind.Image or FileKind.Video or FileKind.Audio))
            throw new ConvertException("Для этого типа файлов инструменты недоступны");

        switch (tool)
        {
            case "compress":
                switch (kind)
                {
                    case FileKind.Image:
                        return ext switch
                        {
                            "png" => await Ffmpeg(input, "png", new() { "-vf", PaletteChain, "-frames:v", "1", "-update", "1" }, p, " (compressed)"),
                            "webp" => await Ffmpeg(input, "webp", new() { "-c:v", "libwebp", "-quality", "65", "-frames:v", "1", "-update", "1" }, p, " (compressed)"),
                            _ => await Ffmpeg(input, "jpg", new() { "-vf", FlattenWhite + ",format=yuvj420p", "-q:v", "7", "-frames:v", "1", "-update", "1" }, p, " (compressed)"),
                        };
                    case FileKind.Video:
                        return await Ffmpeg(input, "mp4", new()
                        {
                            "-c:v", "libx264", "-crf", "28", "-preset", "medium", "-pix_fmt", "yuv420p",
                            "-vf", "scale=-2:'min(720,ih)'", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart",
                        }, p, " (compressed)");
                    default:
                        return await Ffmpeg(input, "mp3", new() { "-vn", "-c:a", "libmp3lame", "-b:a", "128k" }, p, " (compressed)");
                }

            case "strip":
                if (kind == FileKind.Image)
                {
                    var a = new List<string> { "-map_metadata", "-1" };
                    a.AddRange(ImageArgs(ext));
                    return await Ffmpeg(input, ext, a, p, " (clean)");
                }
                return await Ffmpeg(input, ext, new() { "-map_metadata", "-1", "-map_chapters", "-1", "-c", "copy" }, p, " (clean)");

            case "resize":
                return await Ffmpeg(input, ext, WithFilter(kind, ext, "scale=trunc(iw/4)*2:-2"), p, " (50%)");

            case "rotate":
                return await Ffmpeg(input, ext, WithFilter(kind, ext, "transpose=1"), p, " (rotated)");
        }
        throw new ConvertException("Неизвестный инструмент");
    }

    static List<string> WithFilter(FileKind kind, string ext, string vf)
    {
        if (kind == FileKind.Image)
        {
            var a = ImageArgs(ext);
            int i = a.IndexOf("-vf");
            if (i >= 0) a[i + 1] = vf + "," + a[i + 1]; else a.InsertRange(0, new[] { "-vf", vf });
            return a;
        }
        return new() { "-vf", vf, "-c:a", "copy" };
    }

    // ───────────────────────────── ffmpeg ─────────────────────────────

    public static string UniquePath(string dir, string name, string ext)
    {
        string p = Path.Combine(dir, name + "." + ext);
        for (int i = 1; File.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i}).{ext}");
        return p;
    }

    static async Task<string> Ffmpeg(string input, string outExt, List<string> args, IProgress<double>? progress, string suffix = "")
    {
        string dir = Path.GetDirectoryName(input)!;
        string output = UniquePath(dir, Path.GetFileNameWithoutExtension(input) + suffix, outExt);
        var all = new List<string> { "-i", input };
        all.AddRange(args);
        all.Add(output);
        try
        {
            await RunFfmpeg(all, progress);
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            throw;
        }
        return output;
    }

    /// <summary>Runs ffmpeg with the given arguments (without -y/-hide_banner); reports 0..1 progress when it can.</summary>
    public static async Task RunFfmpeg(IEnumerable<string> args, IProgress<double>? progress)
    {
        string exe = FfmpegLocator.Find()
            ?? throw new ConvertException("ffmpeg не найден. Установи его: winget install Gyan.FFmpeg");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-y" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var tail = new Queue<string>();
        double duration = 0;
        proc.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (tail) { tail.Enqueue(e.Data); if (tail.Count > 8) tail.Dequeue(); }
            if (duration <= 0)
            {
                var d = Regex.Match(e.Data, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (d.Success) duration = ToSeconds(d);
            }
            else
            {
                var t = Regex.Match(e.Data, @"time=(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (t.Success) progress?.Report(Math.Clamp(ToSeconds(t) / duration, 0, 1));
            }
        };
        proc.OutputDataReceived += (_, _) => { };
        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            string msg;
            lock (tail) msg = string.Join("\n", tail.TakeLast(3));
            throw new ConvertException("ffmpeg: " + msg);
        }
    }

    static double ToSeconds(Match m) =>
        int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
        + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
}

internal static class FfmpegLocator
{
    static string? _cached;

    public static string? Find()
    {
        if (_cached != null && File.Exists(_cached)) return _cached;

        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg.exe")); } catch { }
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
        string pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(pkgs))
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(pkgs, "*FFmpeg*"))
                    candidates.AddRange(Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories));
            }
            catch { }
        }

        _cached = candidates.FirstOrDefault(File.Exists);
        return _cached;
    }
}
